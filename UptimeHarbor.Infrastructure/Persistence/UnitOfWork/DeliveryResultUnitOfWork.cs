using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

public class DeliveryResultUnitOfWork(IUptimeHarborDbContextSession session,
    IDeliveryResultRepository results)
    : UptimeHarborDbUnitOfWork(session), IDeliveryResultUnitOfWork
{
    public IDeliveryResultRepository Results { get; } = results;
}
