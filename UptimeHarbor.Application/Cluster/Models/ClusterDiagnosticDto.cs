namespace UptimeHarbor.Application.Cluster.Models;

public record ClusterDiagnosticDto(Guid NodeId, Guid? LeaderNodeId, string Role, string Majority,
    int VotingNodes, int RequiredMajority, long AppliedIndex, long? CommittedIndex, long? Lag,
    long Pending, long HeldPending, long DeliveryBacklog, string LocalDatabase,
    string Telegram, string TelegramReason, double? TelegramAgeSeconds, DateTimeOffset AtUtc,
    IReadOnlyList<ClusterNodeDiagnosticDto> Nodes);
