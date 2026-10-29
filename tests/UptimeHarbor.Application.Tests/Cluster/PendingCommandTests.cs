using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class PendingCommandTests
{
    [Fact]
    public async Task ConcurrentScopesShareAdmissionLimit()
    {
        // Arrange
        Fixture fixture = new(new PendingAdmissionGate(1, 0));
        PendingCommandService second = fixture.CreateService();

        // Act
        Exception?[] outcomes = await Task.WhenAll(
            Record.ExceptionAsync(() => fixture.Service.SubmitAsync(fixture.Scenario.Server(), fixture.Scenario.Node,
                TestContext.Current.CancellationToken)).AsTask(),
            Record.ExceptionAsync(() => second.SubmitAsync(fixture.Scenario.Server(), fixture.Scenario.Node,
                TestContext.Current.CancellationToken)).AsTask());

        // Assert
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.Single(outcomes, outcome => outcome is InvalidOperationException { Message: "PendingQueueFull" });
        Assert.Single(fixture.Work.Items);
    }

    [Fact]
    public async Task FullQueueRejectsNewWorkButRetainsRetryAndReservedControlCapacity()
    {
        // Arrange
        Fixture fixture = new(new PendingAdmissionGate(2, 1));
        RevisionCommand original = fixture.Scenario.Server();
        await fixture.Service.SubmitAsync(original, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Act
        Exception? full = await Record.ExceptionAsync(() => fixture.Service.SubmitAsync(fixture.Scenario.Server(),
            fixture.Scenario.Node, TestContext.Current.CancellationToken));
        SubmissionReceipt retry = await fixture.CreateService().SubmitAsync(original, fixture.Scenario.Node,
            TestContext.Current.CancellationToken);
        RevisionCommand cancel = fixture.Scenario.Control(original);
        SubmissionReceipt control = await fixture.Service.SubmitAsync(cancel, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<InvalidOperationException>(full);
        Assert.Equal("PendingQueueFull", full.Message);
        Assert.True(retry.LocalPending);
        Assert.True(control.LocalPending);
        Assert.Equal(2, fixture.Work.Items.Count);
        Assert.Equal(cancel.OperationId, fixture.Work.Items[original.OperationId].RequestedControlId);
    }

    [Fact]
    public async Task PendingRetainsTrustedIdentityAndPayload_AndRetryUsesOriginalActorRevision()
    {
        // Arrange
        Fixture fixture = new();
        RevisionCommand proposal = fixture.Scenario.Server() with { ActorUserId = Guid.NewGuid() };

        // Act
        SubmissionReceipt first = await fixture.Service.SubmitAsync(proposal, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        PendingOperation pending = Assert.Single(fixture.Work.Items.Values);
        AcceptedValue actor = fixture.Scenario.State.Values[fixture.Scenario.Actor];
        fixture.State.Publish(fixture.Scenario.State with
        {
            Values = fixture.Scenario.State.Values.SetItem(actor.Id, actor with { Revision = 9 })
        });
        PendingCommandService restarted = fixture.CreateService();
        SubmissionReceipt retry = await restarted.SubmitAsync(proposal, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(first.LocalPending);
        Assert.True(retry.LocalPending);
        Assert.Equal(fixture.Scenario.Actor, pending.ActorUserId);
        Assert.Equal(fixture.Scenario.Node, pending.OriginNodeId);
        Assert.Equal(1, pending.ActorUserRevision);
        Assert.Equal(0, pending.BaseRevision);
        Assert.Equal(PendingState.LocalPending, pending.State);
        Assert.StartsWith("protected:", pending.ProtectedPayload);
        Assert.Equal(pending.Digest, RevisionCodec.Digest(fixture.Protection.Unprotect(pending.ProtectedPayload)));
        Assert.Single(fixture.Work.Items);
        Assert.Single(fixture.Audit.Events);
        Assert.DoesNotContain(proposal.EntityId, fixture.State.Current!.Values.Keys);
    }

    [Fact]
    public async Task LostAcceptedResponseReturnsOriginalResultAfterActorRevisionChanges()
    {
        // Arrange
        Fixture fixture = new();
        RevisionCommand command = fixture.Scenario.Server();
        await fixture.Service.SubmitAsync(command, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        CommandResult result = fixture.Scenario.Apply(command);
        AcceptedValue actor = fixture.Scenario.State.Values[fixture.Scenario.Actor];
        fixture.State.Publish(fixture.Scenario.State with
        {
            Values = fixture.Scenario.State.Values.SetItem(actor.Id, actor with { Revision = 10 })
        });

        // Act
        SubmissionReceipt retry = await fixture.Service.SubmitAsync(command, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(retry.LocalPending);
        Assert.Equal(result, retry.Result);
        Assert.Single(fixture.Work.Items);
        Assert.Single(fixture.Audit.Events);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SubmitAsync(
            command with { BaseRevision = 10 }, fixture.Scenario.Node, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestoreReconcilesKnownCancellation_AndHoldsUnknownUntilAtomicReplacement()
    {
        // Arrange
        Fixture fixture = new();
        RevisionCommand cancelled = fixture.Scenario.Server(name: "cancelled");
        RevisionCommand unknown = fixture.Scenario.Server(name: "unknown");
        await fixture.Service.SubmitAsync(cancelled, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        await fixture.Service.SubmitAsync(unknown, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        fixture.Scenario.Apply(fixture.Scenario.Control(cancelled));
        fixture.State.Publish(fixture.Scenario.State);

        // Act
        await fixture.Service.ReconcileRestoredAsync(TestContext.Current.CancellationToken);
        RevisionCommand replacement = fixture.Scenario.Control(unknown, fixture.Scenario.Server(unknown.EntityId));
        await fixture.Service.ConfirmRestoredAsync(unknown.OperationId, replacement, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        await fixture.Service.ConfirmRestoredAsync(unknown.OperationId, replacement, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PendingState.Resolved, fixture.Work.Items[cancelled.OperationId].State);
        PendingOperation held = fixture.Work.Items[unknown.OperationId];
        Assert.Equal(PendingState.NeedsUserConfirmation, held.State);
        Assert.Equal(replacement.OperationId, held.RequestedControlId);
        Assert.Equal(PendingState.LocalPending, fixture.Work.Items[replacement.OperationId].State);
        Assert.Throws<InvalidOperationException>(() => held.MarkTransmissionStarted(DateTimeOffset.UnixEpoch));
        Assert.Equal(3, fixture.Work.Items.Count);
        Assert.All(fixture.Work.CreatedInTransaction, Assert.True);
    }

    [Fact]
    public async Task StaleOrDisabledConfirmationDoesNotReleaseHeldOriginal()
    {
        // Arrange
        Fixture fixture = new();
        RevisionCommand command = fixture.Scenario.Server();
        await fixture.Service.SubmitAsync(command, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        await fixture.Service.ReconcileRestoredAsync(TestContext.Current.CancellationToken);
        RevisionCommand stale = fixture.Scenario.Control(command, fixture.Scenario.Server(command.EntityId, 99));

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConfirmRestoredAsync(
            command.OperationId, stale, fixture.Scenario.Node, TestContext.Current.CancellationToken));
        AcceptedValue actor = fixture.Scenario.State.Values[fixture.Scenario.Actor];
        fixture.State.Publish(fixture.Scenario.State with
        {
            Values = fixture.Scenario.State.Values.SetItem(actor.Id, actor with { Value = actor.Value with { Enabled = false } })
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ConfirmRestoredAsync(
            command.OperationId, fixture.Scenario.Control(command, fixture.Scenario.Server(command.EntityId)), fixture.Scenario.Node, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(PendingState.NeedsUserConfirmation, fixture.Work.Items[command.OperationId].State);
        Assert.Single(fixture.Work.Items);
    }

    private class Fixture
    {
        public RevisionScenario Scenario { get; } = new();
        public RevisionStateStore State { get; } = new();
        public MemoryPending Work { get; } = new();
        public PayloadProtection Protection { get; } = new();
        public Audit Audit { get; } = new();
        public PendingCommandService Service { get; }
        private readonly PendingAdmissionGate admission;

        public Fixture(PendingAdmissionGate? admission = null)
        {
            this.admission = admission ?? new PendingAdmissionGate();
            State.Publish(Scenario.State);
            Service = CreateService();
        }

        public PendingCommandService CreateService()
        {
            User user = new(Scenario.Actor);
            return new(Work, user, new AcceptedAdministratorAccessPolicy(user, State), State, Protection, Audit, admission);
        }
    }

    private class User(Guid id) : IUserAccessContext
    {
        public bool IsAuthenticated => true;
        public bool IsAdministrator => false;
        public bool IsEnabled => false;
        public Guid UserId => id;
    }

    private class PayloadProtection : IRevisionPayloadProtection
    {
        public string Protect(ReadOnlySpan<byte> payload)
        {
            return "protected:" + Convert.ToBase64String(payload);
        }

        public byte[] Unprotect(string payload)
        {
            return Convert.FromBase64String(payload[10..]);
        }
    }

    private class Audit : IRevisionAudit
    {
        public List<string> Events { get; } = [];

        public void Record(Guid operationId, Guid actorUserId, Guid originNodeId, string outcome)
        {
            Events.Add(outcome);
        }
    }

    private class MemoryPending : IPendingControlUnitOfWork, IPendingOperationRepository
    {
        private bool inTransaction;
        public Dictionary<Guid, PendingOperation> Items { get; } = [];
        public List<bool> CreatedInTransaction { get; } = [];
        public IPendingOperationRepository PendingOperations => this;

        public Task<PendingOperation?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Items.GetValueOrDefault(id));
        }

        public void Create(PendingOperation operation)
        {
            CreatedInTransaction.Add(inTransaction);
            Items.Add(operation.Id, operation);
        }

        public Task<IReadOnlyList<PendingOperation>> GetPageAsync(int offset, int limit, bool readyOnly,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<PendingOperation>>(Items.Values.Skip(offset).Take(limit).ToArray());
        }

        public Task<IReadOnlyList<PendingOperation>> GetDueAsync(bool priority, DateTimeOffset nowUtc, int limit,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async Task<PendingBacklog> GetBacklogAsync(CancellationToken cancellationToken = default)
        {
            long count = Items.Values.LongCount(item => item.State != PendingState.Resolved);
            await Task.Yield();
            return new PendingBacklog(count, 0, null);
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1);
        }

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            inTransaction = true;
            try
            {
                await action(cancellationToken);
            }
            finally
            {
                inTransaction = false;
            }
        }
    }
}
