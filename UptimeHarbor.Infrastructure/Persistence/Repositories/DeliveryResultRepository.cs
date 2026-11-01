using EFCoreLibrary.Abstractions.Database.Repository.Base;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Repositories;

public class DeliveryResultRepository(
    IContextGetItemByPredicateRepository<CapturedDeliveryResult, UptimeHarborDbContext> reader,
    IContextCreateItemRepository<CapturedDeliveryResult, UptimeHarborDbContext> writer) : IDeliveryResultRepository
{
    public Task<CapturedDeliveryResult?> GetAsync(Guid attemptId, CancellationToken cancellationToken = default)
    {
        return reader.GetItemByPredicateAsync(item => item.Id == attemptId, asNoTracking: false, ct: cancellationToken);
    }

    public void Create(CapturedDeliveryResult result)
    {
        writer.Create(result);
    }
}
