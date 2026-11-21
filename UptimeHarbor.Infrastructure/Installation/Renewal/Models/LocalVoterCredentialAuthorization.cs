using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;

namespace UptimeHarbor.Infrastructure.Installation.Renewal.Models;

/// <summary>Exact accepted authorization и публичный anchor до Bind; не majority proof.</summary>
public record LocalVoterCredentialAuthorization(string RequestDigest, VoterCredentialRotation Rotation,
    AcceptedEnrollmentIssuer Issuer)
{
    public override string ToString()
    {
        return $"LocalVoterCredentialAuthorization {Rotation.AuthorizationOperationId}";
    }
}
