namespace UptimeHarbor.Application.Cluster.Models;

public record CommittedRecord(long Index, long Term, bool IsConfiguration, bool IsSnapshot,
    Func<Stream, CancellationToken, ValueTask> CopyPayloadAsync);
