namespace UptimeHarbor.Application.Cluster.Models;

public record MonitoringEvent(Guid Id, Guid IncidentId, long Sequence, string Kind,
    DateTimeOffset AtUtc, string FrontierDigest, string Payload, long Index, long Term);
