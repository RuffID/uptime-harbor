using UptimeHarbor.Application.Abstractions.UnitOfWork;

namespace UptimeHarbor.Infrastructure.Identity;

public interface ILocalLoginUnitOfWork : IUnitOfWorkScope
{
    Task<ApplicationUser?> FindAsync(Guid userId, CancellationToken token);
}
