using UptimeHarbor.Application.Cluster.Models;
using System.Text.Json.Serialization;

namespace UptimeHarbor.Infrastructure.Installation.Renewal.Models;

/// <summary>Exact accepted Bind evidence. Не означает local install, confirmation или Ready.</summary>
public record LocalVoterCredentialBinding(string IntentDigest, CommandResult Result,
    DateTimeOffset BoundAtUtc, DateTimeOffset OverlapUntilUtc)
{
    [JsonIgnore]
    public long CredentialRevision => Result.Index;

    public override string ToString()
    {
        return $"LocalVoterCredentialBinding {Result.OperationId} {CredentialRevision}";
    }
}
