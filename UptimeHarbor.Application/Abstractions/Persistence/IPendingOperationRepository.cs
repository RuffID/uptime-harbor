using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface IPendingOperationRepository
{
    Task<PendingOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken = default);
    void Create(PendingOperation operation);
}
