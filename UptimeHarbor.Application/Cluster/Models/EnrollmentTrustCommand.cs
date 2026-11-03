namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Закрытые schema-3 команды issuer/genesis; не browser DTO и не произвольная подпись.</summary>
public record EnrollmentTrustCommand(int Version, Guid StorageGeneration, Guid IssuerEpoch,
    AcceptedEnrollmentIssuer? Issuer, GenesisAuthorization? Genesis, OperationalNode? Node,
    OperationalCredential? Credential, long BotId, DateTimeOffset? ReauthenticatedAtUtc)
{
    public DateTimeOffset? CoordinatorAtUtc { get; init; }
    public long CoordinatorTerm { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long GenesisMembershipVersion { get; init; }

    public override string ToString()
    {
        return $"EnrollmentTrustCommand {IssuerEpoch}";
    }
}
