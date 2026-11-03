using UptimeHarbor.Application.Abstractions.UnitOfWork;

namespace UptimeHarbor.Application.Cluster.Onboarding;

public interface IEnrollmentSigningUnitOfWork : IUnitOfWorkScope
{
    IEnrollmentSigningRepository Signing { get; }
}
