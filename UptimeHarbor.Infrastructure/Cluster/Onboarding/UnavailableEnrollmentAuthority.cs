using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

public class UnavailableEnrollmentAuthority : IEnrollmentAuthority
{
    public ValueTask<bool> VerifyAsync(RevisionCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}
