namespace UptimeHarbor.Application.Cluster.Models;

public record PeerCredential(Guid CredentialId, Guid NodeId, Guid StorageGeneration,
    Uri Address, string Fingerprint, bool IsVoting, bool IsAdmission, DateTimeOffset ExpiresAtUtc);
