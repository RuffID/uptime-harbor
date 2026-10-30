using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class ClusterOperationsProjection
{
    public static IReadOnlyList<ProjectionRecord> Changes(RevisionImage before, RevisionImage after)
    {
        List<ProjectionRecord> records = [];
        JournalPosition position = new(new ClusterIdentity(after.ClusterId, after.Generation), after.Index, after.Term);
        long membership = after.Operations.MembershipVersion;
        long previousMembership = before.Operations.MembershipVersion;
        foreach (OperationalNode node in after.Operations.Nodes.Values)
        {
            if (before.Index != 0 && previousMembership == membership && before.Operations.Nodes.GetValueOrDefault(node.NodeId) == node)
            {
                continue;
            }

            records.Add(new ClusterMember(node.NodeId, position, node.StorageGeneration, node.Address,
                node.RaftMemberId, membership, node.Voting && !node.Removed));
        }

        foreach (OperationalCredential credential in after.Operations.Credentials.Values)
        {
            if (before.Index != 0 && before.Operations.Credentials.GetValueOrDefault(credential.Id) == credential)
            {
                continue;
            }

            TrustPurpose purpose = TrustPurpose.Active;
            if (credential.AdmissionId.HasValue)
            {
                purpose = TrustPurpose.Admission;
            }

            PeerTrustCredential projected = new(credential.Id, position, credential.NodeId, credential.Fingerprint,
                credential.PublicCertificate, credential.ExpiresAtUtc, purpose, credential.AdmissionId, credential.AdmissionExpiresAtUtc);
            if (credential.ConsumedAtUtc.HasValue)
            {
                // Протокольное завершение может прийти после истечения приглашения;
                // потребление фиксирует время самой принятой заявки, а не время повторной сверки.
                projected.ConsumeAdmission(credential.ConsumedAtUtc.Value);
            }

            if (credential.RevokedAtUtc.HasValue)
            {
                projected.Revoke(credential.RevokedAtUtc.Value);
            }

            records.Add(projected);
        }

        foreach (MembershipOperation change in after.Operations.Changes.Values)
        {
            if (before.Operations.Changes.GetValueOrDefault(change.Id) == change)
            {
                continue;
            }

            MembershipChangeState state = MembershipChangeState.Requested;
            if (change.ConfirmedVersion.HasValue)
            {
                state = MembershipChangeState.ProtocolCommitted;
                if (change.Remove)
                {
                    state = MembershipChangeState.Removed;
                }
            }

            records.Add(new MembershipChange(change.Id, position, change.NodeId, change.ExpectedVersion, state));
        }

        return records;
    }
}
