using EFCoreLibrary.Abstractions.Database.Repository.Base;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Repositories;

public class PendingOperationRepository(
    IContextGetItemByPredicateRepository<PendingOperation, UptimeHarborDbContext> reader,
    IContextCreateItemRepository<PendingOperation, UptimeHarborDbContext> writer) : IPendingOperationRepository
{
    public Task<PendingOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        return reader.GetItemByPredicateAsync(item => item.Id == operationId, asNoTracking: false, ct: cancellationToken);
    }

    public void Create(PendingOperation operation)
    {
        writer.Create(operation);
    }
}
