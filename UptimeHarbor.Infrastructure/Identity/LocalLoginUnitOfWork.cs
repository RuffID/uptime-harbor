using EFCoreLibrary.Abstractions.Database.Repository.Base;
using UptimeHarbor.Infrastructure.Persistence;
using UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

namespace UptimeHarbor.Infrastructure.Identity;

public class LocalLoginUnitOfWork(IUptimeHarborDbContextSession session,
    IContextGetItemByPredicateRepository<ApplicationUser, UptimeHarborDbContext> users)
    : UptimeHarborDbUnitOfWork(session), ILocalLoginUnitOfWork
{
    public Task<ApplicationUser?> FindAsync(Guid userId, CancellationToken token)
    {
        return users.GetItemByPredicateAsync(user => user.Id == userId, asNoTracking: false, ct: token);
    }
}
