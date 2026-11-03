using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Принятая попытка, отдельная от локальных ключей, профиля и состояния runtime.</summary>
public record OnboardingAttempt(EnrollmentRequest Request, string RequestDigest, Guid ReserveOperationId,
    DateTimeOffset ReservationUntilUtc, OnboardingDelegation Delegation, Guid BootId,
    long ProcessEpoch, DateTimeOffset ProcessLeaseUntilUtc, DateTimeOffset LastProgressAtUtc)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? BootChallenge { get; init; }
    public OnboardingAttemptStatus Status { get; init; } = OnboardingAttemptStatus.Reserved;
    public OnboardingAttemptStatus? ResumeStatus { get; init; }
    public long ProgressIndex { get; init; }
    public Guid? IssueId { get; init; }
    public EnrollmentIssuance? Issuance { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long IssuanceAuthorizationIndex { get; init; }
    public string? CertificateDigest { get; init; }
    public Guid? AdmitOperationId { get; init; }
    public DateTimeOffset? AdmissionUntilUtc { get; init; }
    public string? SafeReason { get; init; }

    public override string ToString()
    {
        return $"OnboardingAttempt {Request.AttemptId} {Status}";
    }
}
