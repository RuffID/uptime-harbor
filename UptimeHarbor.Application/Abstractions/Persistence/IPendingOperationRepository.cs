using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface IPendingOperationRepository
{
    Task<PendingOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken = default);
    void Create(PendingOperation operation);
    Task<IReadOnlyList<PendingOperation>> GetPageAsync(int offset, int limit, bool readyOnly,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingOperation>> GetDueAsync(bool priority, DateTimeOffset nowUtc,
        int limit, CancellationToken cancellationToken = default);
    Task<PendingBacklog> GetBacklogAsync(CancellationToken cancellationToken = default);
}
