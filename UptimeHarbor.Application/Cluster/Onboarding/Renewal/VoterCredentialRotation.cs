using System.Text.Json.Serialization;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

/// <summary>Durable границы ротации. CredentialRevision=0 до Bind, затем его accepted index.</summary>
public record VoterCredentialRotation(VoterCredentialRenewal Renewal, Guid AuthorizationOperationId,
    long AuthorizedIndex, string AuthorizationDigest, DateTimeOffset AuthorizedAtUtc)
{
    public Guid? BindOperationId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ContinuationRequestId { get; init; }
    public long CredentialRevision { get; init; }
    public string? BindDigest { get; init; }
    public DateTimeOffset? BoundAtUtc { get; init; }
    public DateTimeOffset? OverlapUntilUtc { get; init; }
    public Guid? RetireOperationId { get; init; }
    public long? RetiredIndex { get; init; }
    public string? RetireDigest { get; init; }
    public DateTimeOffset? RetiredAtUtc { get; init; }
    public string? InstallationProofDigest { get; init; }

    public override string ToString()
    {
        return $"VoterCredentialRotation {Renewal.Issuance.IssueId}";
    }
}
