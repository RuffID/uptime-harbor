namespace UptimeHarbor.Application.Cluster.Models;

public record ClusterDiagnosticDto(Guid NodeId, Guid? LeaderNodeId, string Role, string Majority,
    int VotingNodes, int RequiredMajority, long AppliedIndex, long? CommittedIndex, long? Lag,
    long Pending, long HeldPending, long DeliveryBacklog, string LocalDatabase,
    string Telegram, string TelegramReason, double? TelegramAgeSeconds, DateTimeOffset AtUtc,
    IReadOnlyList<ClusterNodeDiagnosticDto> Nodes)
{
    public long? Term { get; init; }
    public long? MembershipVersion { get; init; }
    public long OperationsRevision { get; init; }
    public Guid MaintenanceId { get; init; }
    public long Conflicts { get; init; }
    public long TerminalDeliveryErrors { get; init; }
    public DateTimeOffset? LastSuccessfulSynchronizationAtUtc { get; init; }
    public double? ProjectionAgeSeconds { get; init; }
}
