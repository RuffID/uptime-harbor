using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface IDeliveryResultRepository
{
    Task<CapturedDeliveryResult?> GetAsync(Guid attemptId, CancellationToken cancellationToken = default);
    void Create(CapturedDeliveryResult result);
}
