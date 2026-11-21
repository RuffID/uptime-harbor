using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

/// <summary>Accepted registry policy; не доказывает majority, PoP, CSR или TLS chain.</summary>
public static class VoterCredentialPolicy
{
    public static VoterCredentialRotation? Latest(RevisionImage image, Guid nodeId)
    {
        return image.OnboardingContracts?.VoterCredentialRotations?.Values
            .Where(item => item.Renewal.Issuance.NodeId == nodeId && item.CredentialRevision > 0)
            .MaxBy(item => item.CredentialRevision);
    }

    public static bool IsUsable(RevisionImage image, OperationalCredential credential, DateTimeOffset now)
    {
        StorageGuards.Utc(now);
        OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(credential.NodeId);
        if (image.Operations.Credentials.GetValueOrDefault(credential.Id) != credential
            || node is null || node.Removed || credential.RevokedAtUtc.HasValue || now >= credential.ExpiresAtUtc)
        {
            return false;
        }

        foreach (VoterCredentialRotation rotation in image.OnboardingContracts?.VoterCredentialRotations?.Values ?? [])
        {
            if (rotation.Renewal.PreviousCredentialId == credential.Id && rotation.CredentialRevision > 0
                && (rotation.RetiredAtUtc.HasValue || rotation.RetiredIndex.HasValue
                    || rotation.BoundAtUtc is not { } boundAt || now < boundAt
                    || rotation.OverlapUntilUtc != OverlapUntil(boundAt, credential.ExpiresAtUtc)
                    || now >= rotation.OverlapUntilUtc))
            {
                return false;
            }
        }

        return true;
    }

    public static DateTimeOffset OverlapUntil(DateTimeOffset boundAtUtc, DateTimeOffset previousExpiry)
    {
        StorageGuards.Utc(boundAtUtc);
        StorageGuards.Utc(previousExpiry);
        DateTimeOffset deadline = boundAtUtc.AddHours(24);
        if (previousExpiry < deadline)
        {
            return previousExpiry;
        }

        return deadline;
    }
}
