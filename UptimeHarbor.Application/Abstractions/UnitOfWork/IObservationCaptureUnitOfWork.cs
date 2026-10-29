using UptimeHarbor.Application.Abstractions.Persistence;

namespace UptimeHarbor.Application.Abstractions.UnitOfWork;

public interface IObservationCaptureUnitOfWork : IUnitOfWorkScope
{
    IObservationCaptureRepository Observations { get; }
}
