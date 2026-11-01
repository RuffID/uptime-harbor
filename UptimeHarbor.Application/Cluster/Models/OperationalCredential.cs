namespace UptimeHarbor.Application.Cluster.Models;

public record OperationalCredential(Guid Id, Guid NodeId, string Fingerprint, string PublicCertificate,
    DateTimeOffset ExpiresAtUtc, Guid? AdmissionId, DateTimeOffset? AdmissionExpiresAtUtc,
    DateTimeOffset? ConsumedAtUtc = null, DateTimeOffset? RevokedAtUtc = null);
