using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;

namespace UptimeHarbor.Infrastructure.Installation.Renewal.Models;

internal sealed record LocalVoterCredentialReplay(LocalVoterCredentialBinding Binding, ProjectionHead Head,
    LocalVoterPendingBindReplay? Pending = null)
{
    internal void RequirePendingEvidence(LocalVoterCredentialState local, RevisionImage image)
    {
        if (Pending is { } pending && (local.Intent != pending.Material
            || image.Results.GetValueOrDefault(pending.Authorization.OperationId) != pending.Authorization))
        {
            throw new InvalidDataException("VoterCredentialPendingBindChanged");
        }
    }

    internal bool Matches(RevisionImage image, Guid nodeId, Guid issueId)
    {
        VoterCredentialRotation? latest = VoterCredentialPolicy.Latest(image, nodeId);
        if (latest is null || latest.Renewal.Issuance.IssueId != issueId
            || latest.CredentialRevision != Binding.CredentialRevision)
        {
            return false;
        }

        return latest.BindOperationId == Binding.Result.OperationId && latest.BindDigest == Binding.Result.Digest
            && latest.BoundAtUtc == Binding.BoundAtUtc && latest.OverlapUntilUtc == Binding.OverlapUntilUtc
            && image.Results.GetValueOrDefault(Binding.Result.OperationId) == Binding.Result;
    }

    internal static void RequireAtLeast(ProjectionHead current, ProjectionHead floor)
    {
        if (current.ClusterId != floor.ClusterId || current.Generation != floor.Generation
            || current.Index < floor.Index || current.Term < floor.Term
            || current.Index == floor.Index && (current.Term != floor.Term || current.Digest != floor.Digest))
        {
            throw new InvalidDataException("VoterCredentialLocalInstallationCutChanged");
        }
    }
}
