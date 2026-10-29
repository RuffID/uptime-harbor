using System.Collections.Immutable;

namespace UptimeHarbor.Application.Cluster.Models;

public record MonitoringSummary(Guid Id, Guid PlanId, Guid ChatId, int PartNumber, long CutIndex,
    Guid IntentId, string Text, ImmutableArray<Guid> SourceIntentIds, long Index, long Term);
