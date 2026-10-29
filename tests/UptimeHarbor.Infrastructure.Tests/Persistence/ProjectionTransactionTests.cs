using Microsoft.EntityFrameworkCore.Storage;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Persistence.UnitOfWork;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Persistence;

public class ProjectionTransactionTests
{
    [Fact]
    public async Task CommitAndReplay_KeepOneProjectionAndStableCursor()
    {
        // Arrange
        FakeProjection repository = new();
        FakeSession session = new(repository);
        ProjectionApplyUnitOfWork unitOfWork = new(session, repository);
        ProjectionWriteSet changes = Changes();

        // Act
        bool first = await unitOfWork.ApplyAsync(changes, TestContext.Current.CancellationToken);
        bool replay = await unitOfWork.ApplyAsync(changes, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(first);
        Assert.False(replay);
        Assert.Equal(1, repository.StageCount);
        Assert.Equal(changes.Receipt.LogIndex, repository.SavedCursor!.LogIndex);
        Assert.Same(changes, repository.SavedChanges);
        Assert.Equal(["begin", "stage", "save", "commit", "begin", "save", "commit"], session.Events);
    }

    [Fact]
    public async Task FailureBeforeCommit_RollsBackEffectsAndCursor_ThenRetrySucceeds()
    {
        // Arrange
        FakeProjection repository = new();
        FakeSession session = new(repository) { FailOnSave = true };
        ProjectionApplyUnitOfWork unitOfWork = new(session, repository);
        ProjectionWriteSet changes = Changes();

        // Act
        Exception? failure = await Record.ExceptionAsync(() => unitOfWork.ApplyAsync(changes, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Null(repository.SavedCursor);
        Assert.Null(repository.SavedReceipt);
        Assert.Null(repository.SavedChanges);
        Assert.Equal(["begin", "stage", "save", "rollback", "clear"], session.Events);

        // Act
        session.FailOnSave = false;
        bool retried = await unitOfWork.ApplyAsync(changes, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(retried);
        Assert.Same(changes, repository.SavedChanges);
        Assert.Equal(changes.Receipt.LogIndex, repository.SavedCursor!.LogIndex);
    }

    [Fact]
    public async Task ReplayWithChangedDigest_FailsWithoutReapplying()
    {
        // Arrange
        FakeProjection repository = new();
        FakeSession session = new(repository);
        ProjectionApplyUnitOfWork unitOfWork = new(session, repository);
        ProjectionWriteSet changes = Changes();
        await unitOfWork.ApplyAsync(changes, TestContext.Current.CancellationToken);
        ProjectionWriteSet corrupt = new()
        {
            NodeId = changes.NodeId,
            ExpectedCursorIndex = 0,
            Receipt = new AppliedJournalReceipt(changes.Receipt.Id, changes.Receipt.GetPosition(), new string('B', 64))
        };

        // Act
        Exception? failure = await Record.ExceptionAsync(() => unitOfWork.ApplyAsync(corrupt, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(1, repository.StageCount);
        Assert.Equal(changes.Receipt.Digest, repository.SavedReceipt!.Digest);
    }

    private static ProjectionWriteSet Changes()
    {
        JournalPosition position = new(new ClusterIdentity(Guid.NewGuid(), 1), 7, 1);
        return new ProjectionWriteSet
        {
            NodeId = Guid.NewGuid(),
            ExpectedCursorIndex = 0,
            Receipt = new AppliedJournalReceipt(Guid.NewGuid(), position, new string('A', 64)),
            Account = new SharedAccount(Guid.NewGuid(), Guid.NewGuid(), "admin",
                new ProtectedCredential("hash", 1, "security", "concurrency"), true, 7)
        };
    }

    private class FakeProjection : IProjectionRepository
    {
        public ProjectionCursor? SavedCursor { get; private set; }
        public AppliedJournalReceipt? SavedReceipt { get; private set; }
        public ProjectionWriteSet? SavedChanges { get; private set; }
        public ProjectionCursor? WorkingCursor { get; private set; }
        public AppliedJournalReceipt? WorkingReceipt { get; private set; }
        public ProjectionWriteSet? WorkingChanges { get; private set; }
        public List<string> Events { get; } = [];
        public int StageCount { get; private set; }

        public void Begin()
        {
            WorkingCursor = null;
            if (SavedCursor is not null)
            {
                WorkingCursor = new ProjectionCursor(SavedCursor.Id,
                    new ClusterIdentity(SavedCursor.ClusterId, SavedCursor.ClusterGeneration));
                WorkingCursor.Advance(0, new JournalPosition(
                    new ClusterIdentity(SavedCursor.ClusterId, SavedCursor.ClusterGeneration), SavedCursor.LogIndex, SavedCursor.Term),
                    SavedCursor.Digest!);
            }

            WorkingReceipt = SavedReceipt;
            WorkingChanges = SavedChanges;
        }

        public void Commit()
        {
            SavedCursor = WorkingCursor;
            SavedReceipt = WorkingReceipt;
            SavedChanges = WorkingChanges;
        }

        public Task<ProjectionCursor?> GetCursorAsync(Guid nodeId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(WorkingCursor);
        }

        public Task<AppliedJournalReceipt?> GetReceiptAsync(JournalPosition position, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(WorkingReceipt);
        }

        public Task<OperationDecision?> GetDecisionAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<OperationDecision?>(null);
        }

        public Task StageAsync(ProjectionWriteSet changes, CancellationToken cancellationToken = default)
        {
            Events.Add("stage");
            StageCount++;
            WorkingChanges = changes;
            return Task.CompletedTask;
        }

        public void AddCursor(ProjectionCursor cursor)
        {
            WorkingCursor = cursor;
        }

        public void AddReceipt(AppliedJournalReceipt receipt)
        {
            WorkingReceipt = receipt;
        }
    }

    private class FakeSession(FakeProjection repository) : IUptimeHarborDbContextSession
    {
        public bool FailOnSave { get; set; }
        public List<string> Events { get; } = repository.Events;

        public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
        {
            Events.Add("begin");
            repository.Begin();
            return Task.FromResult<IDbContextTransaction>(new FakeTransaction(repository));
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            Events.Add("save");
            if (FailOnSave)
            {
                throw new InvalidOperationException("Injected save failure.");
            }

            return Task.FromResult(1);
        }

        public void Clear()
        {
            Events.Add("clear");
        }
    }

    private class FakeTransaction(FakeProjection repository) : IDbContextTransaction
    {
        public Guid TransactionId { get; } = Guid.NewGuid();

        public void Commit()
        {
            repository.Events.Add("commit");
            repository.Commit();
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            Commit();
            return Task.CompletedTask;
        }

        public void Rollback()
        {
            repository.Events.Add("rollback");
            repository.Begin();
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            Rollback();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
