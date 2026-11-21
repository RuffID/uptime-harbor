using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

/// <summary>Typed accepted payload. InstallationProofDigest относится только к RetireOldCredential.</summary>
public record VoterCredentialCommand(VoterCredentialRenewal Renewal, OperationalCredential? Credential,
    string? InstallationProofDigest)
{
    public override string ToString()
    {
        return $"VoterCredentialCommand {Renewal.Issuance.IssueId}";
    }
}
