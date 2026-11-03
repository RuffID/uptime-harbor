namespace UptimeHarbor.Application.Cluster.Models;

public record AcceptedOnlineGrant(OnlineGrant Grant, long AuthorizedIndex, string AuthorizedDigest, long? ConsumedIndex,
    string? ConsumedDigest)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ProcessRenewal { get; init; }
}
