using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

/// <summary>Immutable renewal intent; не содержит invitation/attempt и не является PoP authority.</summary>
public record VoterCredentialRenewal(Guid PreviousCredentialId, string PreviousFingerprint,
    string PreviousSpkiDigest, long PreviousCredentialRevision, EnrollmentIssuance Issuance)
{
    public override string ToString()
    {
        return $"VoterCredentialRenewal {Issuance.IssueId}";
    }
}
