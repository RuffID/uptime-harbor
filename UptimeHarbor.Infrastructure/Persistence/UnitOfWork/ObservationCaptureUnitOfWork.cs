using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

public class ObservationCaptureUnitOfWork(IUptimeHarborDbContextSession session,
    IObservationCaptureRepository observations)
    : UptimeHarborDbUnitOfWork(session), IObservationCaptureUnitOfWork
{
    public IObservationCaptureRepository Observations { get; } = observations;
}
