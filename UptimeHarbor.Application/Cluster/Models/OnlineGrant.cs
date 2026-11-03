namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Purpose-bound signed template. Подпись без accepted записи не предоставляет полномочий.</summary>
public record OnlineGrant(int Version, OnlineGrantPurpose Purpose, Guid ClusterId, long Generation,
    Guid AttemptId, Guid? InvitationId, Guid? AdmitOperationId, Guid NodeId, Guid StorageGeneration,
    string PeerUri, string SpkiDigest, Guid CredentialId, string LeafDigest, Guid IssuerEpoch,
    Guid GrantId, string RequestDigest, Guid BootId, string BootChallenge, long ProcessEpoch,
    Guid ActorId, long ActorRevision, long DelegationRevision, long RecoveryVersion,
    long OperationsRevision, long MembershipVersion, long MinimumIndex, string? SnapshotDigest,
    DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc, string Signature)
{
    public override string ToString()
    {
        return $"OnlineGrant {GrantId}/{Purpose}";
    }
}
