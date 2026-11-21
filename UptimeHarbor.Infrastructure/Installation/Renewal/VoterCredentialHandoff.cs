using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

public sealed record VoterCredentialHandoff(Guid IssueId, long CredentialRevision, string BindDigest, string Fingerprint)
{
    public void RequireSubject(RevisionImage image, Guid nodeId, Guid storage, DateTimeOffset now)
    {
        RequireSubject(image, nodeId, storage, now, allowRetired: false);
    }

    internal VoterCredentialRotation RequireSubject(RevisionImage image, Guid nodeId, Guid storage, DateTimeOffset now,
        bool allowRetired)
    {
        OperationalNode node = EnrollmentPeerAuthority.RequireVoter(image, nodeId, storage);
        VoterCredentialRotation? rotation = VoterCredentialPolicy.Latest(image, nodeId);
        if (!node.Activated || rotation is null || rotation.Renewal.Issuance.IssueId != IssueId
            || rotation.Renewal.Issuance.StorageGeneration != storage || rotation.Renewal.Issuance.PeerUri != node.Address
            || rotation.CredentialRevision != CredentialRevision || rotation.BindDigest != BindDigest
            || rotation.BoundAtUtc is not { } bound || bound > now || !allowRetired && rotation.RetiredIndex.HasValue
            || rotation.BindOperationId is not Guid operation
            || image.Results.GetValueOrDefault(operation) is not { Outcome: OperationOutcome.Committed } result
            || result.OperationId != operation || result.Index != CredentialRevision || result.AppliedRevision != CredentialRevision
            || result.Digest != BindDigest
            || result.Index > image.Index || result.Term <= 0 || result.Term > image.Term)
        {
            throw new UnauthorizedAccessException("VoterCredentialHandoffBindChanged");
        }

        RequireLeaf(image, node, IssueId, Fingerprint, now);
        return rotation;
    }

    public static void RequireLeaf(RevisionImage image, OperationalNode node, Guid id, string fingerprint, DateTimeOffset now)
    {
        OperationalCredential? credential = image.Operations.Credentials.GetValueOrDefault(id);
        if (!node.Activated || !node.Voting || node.Removed || credential is null || credential.Fingerprint != fingerprint
            || credential.AdmissionId.HasValue && !credential.ConsumedAtUtc.HasValue
            || !EnrollmentPeerTrustPolicy.IsActive(image, node, credential, now))
        {
            throw new UnauthorizedAccessException("VoterCredentialHandoffLeafChanged");
        }
    }

    public override string ToString()
    {
        return $"VoterCredentialHandoff {IssueId} {CredentialRevision}";
    }
}

public interface IVoterCredentialHandoffPeer
{
    Task ConfirmAsync(OperationalNode witness, VoterCredentialHandoff handoff, DynamicPeerTls.Snapshot snapshot,
        RevisionImage image, CancellationToken token);
}
