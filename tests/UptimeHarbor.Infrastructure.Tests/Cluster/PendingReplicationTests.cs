using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class PendingReplicationTests
{
    [Fact]
    public async Task PriorityRetryBypassesMinuteCycle_BoundedConfigurationQueueAndHeldBackupRemainVisible()
    {
        // Arrange
        RevisionFixture fixture = new();
        MemoryPending work = new();
        RevisionCommand priority = fixture.Server() with { EntityKind = DeletedEntityKind.Account };
        work.Add(priority);
        work.Add(fixture.Server());
        work.Add(fixture.Server());
        work.Add(fixture.Server());
        PendingOperation held = work.Add(fixture.Server());
        held.RequireConfirmationAfterRestore();
        RecordingTransport transport = new() { InvalidPriorityResponse = true };
        Clock clock = new();
        RevisionStateStore state = new();
        state.Publish(fixture.Baseline);
        ClusterOptions options = fixture.Options("unused");
        options.BatchMaxEntries = 2;
        ServiceCollection services = new();
        services.AddSingleton<IPendingControlUnitOfWork>(work);
        services.AddSingleton<IPendingOperationRepository>(work);
        services.AddSingleton<IRevisionPayloadProtection, Protection>();
        using ServiceProvider provider = services.BuildServiceProvider();
        Audit audit = new();
        PendingReplicationPump pump = new(provider.GetRequiredService<IServiceScopeFactory>(), transport, state,
            new Runtime(), Options.Create(options), clock, audit);

        // Act
        await pump.RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PendingState.AwaitingDecision, work.Items[priority.OperationId].State);
        Assert.Equal(5, pump.Progress.Pending);
        Assert.Equal("PeerOrDecisionUnavailable", pump.Progress.DelayReason);
        Assert.Empty(audit.Outcomes);

        // Act
        clock.Advance(5);
        transport.InvalidPriorityResponse = false;
        await pump.RunOnceAsync(TestContext.Current.CancellationToken);
        await pump.RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, work.Items[priority.OperationId].SubmissionAttempts);
        Assert.Equal(PendingState.Resolved, work.Items[priority.OperationId].State);
        Assert.Equal(2, pump.Progress.Pending);
        Assert.Equal(1, pump.Progress.AwaitingConfirmation);
        Assert.Equal(3, audit.Outcomes.Count);
        Assert.All(transport.Batches, batch => Assert.InRange(batch.Count, 1, 2));
        Assert.All(transport.Batches.Take(2), batch => Assert.Equal(priority.OperationId, Assert.Single(batch).OperationId));

        // Act
        clock.Advance(60);
        await pump.RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, pump.Progress.Pending);
        Assert.Equal(PendingState.NeedsUserConfirmation, held.State);
        Assert.NotNull(pump.Progress.OldestPendingAtUtc);
        Assert.Equal(4, audit.Outcomes.Count);
        Assert.Equal((priority.OperationId, priority.ActorUserId, priority.EntityId, priority.OriginNodeId, "Committed"),
            Assert.Single(audit.Accounts));
        Assert.DoesNotContain(transport.Batches.SelectMany(batch => batch), command => command.OperationId == held.Id);
    }

    private class Runtime : IClusterStateView
    {
        public ConsensusStatus? Consensus => null;
        public AcceptedClusterState Accepted { get; } = new(true, true, true, false, false, 2, 1, Guid.Empty, false);
    }

    private class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return ticks;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.UnixEpoch.AddTicks(ticks);
        }

        public void Advance(int seconds)
        {
            ticks += TimeSpan.FromSeconds(seconds).Ticks;
        }
    }

    private class RecordingTransport : IRevisionTransport
    {
        public bool InvalidPriorityResponse { get; set; }
        public List<IReadOnlyList<RevisionCommand>> Batches { get; } = [];

        public Task<IReadOnlyList<CommandResult>> SendAsync(IReadOnlyList<RevisionCommand> commands, CancellationToken token)
        {
            Batches.Add(commands);
            if (InvalidPriorityResponse)
            {
                return Task.FromResult<IReadOnlyList<CommandResult>>([]);
            }

            return Task.FromResult<IReadOnlyList<CommandResult>>(commands.Select(command => new CommandResult(command.OperationId,
                RevisionCodec.Digest(command), OperationOutcome.Committed, 3, 1, 3)).ToArray());
        }
    }

    private class Protection : IRevisionPayloadProtection
    {
        public string Protect(ReadOnlySpan<byte> payload)
        {
            return Convert.ToBase64String(payload);
        }

        public byte[] Unprotect(string payload)
        {
            return Convert.FromBase64String(payload);
        }
    }

    private class Audit : IRevisionAudit
    {
        public List<string> Outcomes { get; } = [];
        public List<(Guid Operation, Guid Actor, Guid Subject, Guid Node, string Outcome)> Accounts { get; } = [];

        public void RecordAccount(Guid operationId, Guid actorUserId, Guid subjectUserId, Guid nodeId, string outcome)
        {
            Accounts.Add((operationId, actorUserId, subjectUserId, nodeId, outcome));
            Outcomes.Add(outcome);
        }

        public void Record(Guid operationId, Guid actorUserId, Guid nodeId, string outcome)
        {
            Outcomes.Add(outcome);
        }
    }

    private class MemoryPending : IPendingControlUnitOfWork, IPendingOperationRepository
    {
        public Dictionary<Guid, PendingOperation> Items { get; } = [];
        public IPendingOperationRepository PendingOperations => this;

        public PendingOperation Add(RevisionCommand command)
        {
            PendingOperation item = new(new ClusterIdentity(command.ClusterId, command.ClusterGeneration),
                new CommandIdentity(command.OperationId, command.OriginNodeId, command.ActorUserId, null),
                $"{command.EntityKind}.{command.Action}", command.EntityId, command.BaseRevision, command.ActorRevision,
                RevisionCodec.Digest(command), Convert.ToBase64String(RevisionCodec.Encode(command)), command.CreatedAtUtc);
            Create(item);
            return item;
        }

        public void Create(PendingOperation operation)
        {
            Items.Add(operation.Id, operation);
        }

        public Task<PendingOperation?> GetAsync(Guid id, CancellationToken token = default)
        {
            return Task.FromResult(Items.GetValueOrDefault(id));
        }

        public Task<IReadOnlyList<PendingOperation>> GetPageAsync(int offset, int limit, bool readyOnly, CancellationToken token = default)
        {
            return Task.FromResult<IReadOnlyList<PendingOperation>>(Items.Values.Skip(offset).Take(limit).ToArray());
        }

        public Task<IReadOnlyList<PendingOperation>> GetDueAsync(bool priority, DateTimeOffset now, int limit, CancellationToken token = default)
        {
            return Task.FromResult<IReadOnlyList<PendingOperation>>(Items.Values.Where(item => item.State is not
                    (PendingState.Resolved or PendingState.NeedsUserConfirmation) && item.RequestedControlId is null)
                .Where(item => item.NextAttemptAtUtc is null || item.NextAttemptAtUtc <= now)
                .Where(item => item.Kind.StartsWith("Account.", StringComparison.Ordinal) == priority).Take(limit).ToArray());
        }

        public Task<PendingBacklog> GetBacklogAsync(CancellationToken token = default)
        {
            PendingOperation[] remaining = Items.Values.Where(item => item.State != PendingState.Resolved).ToArray();
            return Task.FromResult(new PendingBacklog(remaining.Length,
                remaining.Count(item => item.State == PendingState.NeedsUserConfirmation),
                remaining.Select(item => (DateTimeOffset?)item.CreatedAtUtc).Min()));
        }

        public Task<int> SaveChangesAsync(CancellationToken token = default)
        {
            return Task.FromResult(1);
        }

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken token = default)
        {
            await action(token);
        }
    }
}
