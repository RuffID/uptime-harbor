using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

internal class PendingMemory : IPendingControlUnitOfWork, IPendingOperationRepository
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
