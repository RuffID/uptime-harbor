using UptimeHarbor.Application.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

public class EnrollmentSigningUnitOfWork(IUptimeHarborDbContextSession session, IEnrollmentSigningRepository signing)
    : UptimeHarborDbUnitOfWork(session), IEnrollmentSigningUnitOfWork
{
    public IEnrollmentSigningRepository Signing { get; } = signing;
}
