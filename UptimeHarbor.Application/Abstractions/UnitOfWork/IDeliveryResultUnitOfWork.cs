using UptimeHarbor.Application.Abstractions.Persistence;

namespace UptimeHarbor.Application.Abstractions.UnitOfWork;

public interface IDeliveryResultUnitOfWork : IUnitOfWorkScope
{
    IDeliveryResultRepository Results { get; }
}
