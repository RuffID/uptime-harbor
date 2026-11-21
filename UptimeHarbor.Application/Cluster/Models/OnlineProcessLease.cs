namespace UptimeHarbor.Application.Cluster.Models;

public record OnlineProcessLease(Guid NodeId, Guid GrantId, Guid BootId, string BootChallenge,
    long ProcessEpoch, long RecoveryVersion, DateTimeOffset LeaseUntilUtc, long Index)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool CredentialContinuation { get; init; }
}
