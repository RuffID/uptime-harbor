using UptimeHarbor.Application.Abstractions.Persistence;

namespace UptimeHarbor.Application.Abstractions.UnitOfWork;

public interface IProjectionApplyUnitOfWork : IUnitOfWorkScope
{
    Task<bool> ApplyAsync(ProjectionWriteSet changes, CancellationToken cancellationToken = default);
}
