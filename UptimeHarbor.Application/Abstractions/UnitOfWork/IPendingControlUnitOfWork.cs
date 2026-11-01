using UptimeHarbor.Application.Abstractions.Persistence;

namespace UptimeHarbor.Application.Abstractions.UnitOfWork;

public interface IPendingControlUnitOfWork : IUnitOfWorkScope
{
    IPendingOperationRepository PendingOperations { get; }
}
