using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Неизменяемая команда доверенного peer-протокола; actor устанавливает серверная граница.</summary>
public record RevisionCommand
{
    public int Schema { get; init; } = 1;
    public required Guid ClusterId { get; init; }
    public required long ClusterGeneration { get; init; }
    public required Guid OperationId { get; init; }
    public required Guid OriginNodeId { get; init; }
    public required Guid ActorUserId { get; init; }
    public required long ActorRevision { get; init; }
    public required Guid EntityId { get; init; }
    public required DeletedEntityKind EntityKind { get; init; }
    public required string Action { get; init; }
    public required long BaseRevision { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public ConfigurationValue? Value { get; init; }
    public RevisionCommand? Target { get; init; }
    public RevisionCommand? Replacement { get; init; }
    public Guid? ResolvesConflictId { get; init; }
    public string? AccountAction { get; init; }
    public string? SystemSource { get; init; }
    public MonitoringCommand? Monitoring { get; init; }
    public DeliveryCommand? Delivery { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ClusterOperation? Operation { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public OnboardingCommand? Onboarding { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public EnrollmentTrustCommand? EnrollmentTrust { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public OnlineLifecycleCommand? OnlineLifecycle { get; init; }

    public override string ToString()
    {
        return $"RevisionCommand {OperationId}";
    }
}
