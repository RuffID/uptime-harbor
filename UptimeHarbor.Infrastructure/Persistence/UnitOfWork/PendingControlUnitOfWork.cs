using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

public class PendingControlUnitOfWork(IUptimeHarborDbContextSession session,
    IPendingOperationRepository pendingOperations)
    : UptimeHarborDbUnitOfWork(session), IPendingControlUnitOfWork
{
    public IPendingOperationRepository PendingOperations { get; } = pendingOperations;
}
