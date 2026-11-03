namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Ответ только привязанному ключу через исходный browser TLS origin. Общих account данных здесь нет.</summary>
public record EnrollmentDescriptor(int Version, EnrollmentDiscovery Discovery, EnrollmentRequest Request,
    string RequestDigest, Guid BootId, string BootChallenge, long ProcessEpoch, string Nonce,
    long ReservationIndex, long AppliedIndex, long BotId, DateTimeOffset ReservationUntilUtc,
    DateTimeOffset ProcessLeaseUntilUtc, DateTimeOffset? AdmissionUntilUtc, string Status,
    EnrollmentIssuance? Issuance, string? CertificateDer, AcceptedEnrollmentIssuer[] Issuers,
    OperationalNode[] Nodes, OperationalCredential[] Credentials)
{
    public EnrollmentProof? ResponseProof { get; init; }
}
