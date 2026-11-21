using System.Text.Json.Serialization;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

/// <summary>Immutable voter intent. OperationId/IssueId не зависят от процесса или лидера.</summary>
public record VoterCredentialRequest(int Version, Guid ClusterId, long Generation, Guid OperationId,
    VoterCredentialRenewal Renewal)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VoterContinuationBoundSource? BoundSource { get; init; }

    public override string ToString()
    {
        return $"VoterCredentialRequest {OperationId}";
    }
}
