using System.Text.Json.Serialization;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

public record VoterContinuationCommand(VoterContinuationContext Context, string? PriorDigest,
    VoterContinuationEvidence? Evidence, VoterContinuationAcceptance? Acceptance)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OperationalCredential? Credential { get; init; }

    public override string ToString()
    {
        return $"VoterContinuationCommand {Context.Request.OperationId}";
    }
}
