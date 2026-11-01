namespace UptimeHarbor.Infrastructure.Cluster;

public record PeerEnvelope(Guid ClusterId, long Generation, Guid FromNodeId, Guid ToNodeId,
    Guid StorageGeneration, Guid CredentialId, Guid RequestId, DateTimeOffset IssuedAtUtc,
    string Compatibility, Guid Challenge, long Term = 0);
