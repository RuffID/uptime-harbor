using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding;

public interface IOnlineGrantSigningAuthorization
{
    ValueTask ConfirmAsync(OnlineGrant grant, EnrollmentIssuerReference localIssuer, CancellationToken token);
}
