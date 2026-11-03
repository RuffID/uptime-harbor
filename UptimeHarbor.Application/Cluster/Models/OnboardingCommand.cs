using System.Text.Json.Serialization;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Закрытый payload согласования. Не принимать его напрямую из browser JSON.</summary>
public record OnboardingCommand
{
    [JsonRequired]
    public int Version { get; init; } = 1;
    public required Guid StorageGeneration { get; init; }
    public required Guid InvitationId { get; init; }
    [JsonRequired]
    public Guid? ReplacesInvitationId { get; init; }
    [JsonRequired]
    public string? Verifier { get; init; }
    [JsonRequired]
    public EnrollmentRequest? Request { get; init; }
    [JsonRequired]
    public string? RequestDigest { get; init; }
    [JsonRequired]
    public Guid AttemptId { get; init; }
    [JsonRequired]
    public Guid BootId { get; init; }
    [JsonRequired]
    public long ProcessEpoch { get; init; }
    [JsonRequired]
    public DateTimeOffset? ReauthenticatedAtUtc { get; init; }
    [JsonRequired]
    public Guid? IssueId { get; init; }
    [JsonRequired]
    public EnrollmentIssuance? Issuance { get; init; }
    [JsonRequired]
    public string? CertificateDigest { get; init; }
    [JsonRequired]
    public long ProgressIndex { get; init; }
    [JsonRequired]
    public Guid? MembershipChangeId { get; init; }
    [JsonRequired]
    public long MembershipVersion { get; init; }
    [JsonRequired]
    public OperationalNode? Node { get; init; }
    [JsonRequired]
    public OperationalCredential? Credential { get; init; }
    [JsonRequired]
    public DateTimeOffset? CoordinatorAtUtc { get; init; }
    [JsonRequired]
    public long CoordinatorTerm { get; init; }

    public override string ToString()
    {
        return $"OnboardingCommand {InvitationId}/{AttemptId}";
    }
}
