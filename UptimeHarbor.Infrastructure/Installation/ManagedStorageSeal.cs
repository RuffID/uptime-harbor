namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Локальный зашифрованный checkpoint provenance; не accepted grant и не isolation evidence.</summary>
public record ManagedStorageSeal(int Version, Guid InstallIdentity, Guid AttemptId, Guid ClusterId, long Generation,
    Guid NodeId, Guid StorageGeneration, string PeerAddress, string KeyReference, Guid CredentialId,
    string TrustAnchorFingerprint, long ProfileRevision, string ManifestDigest, Guid SnapshotId,
    string SnapshotDigest, long Index, long Term, string HeadDigest, long MembershipVersion);
