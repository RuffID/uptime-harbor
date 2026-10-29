namespace UptimeHarbor.Application.Cluster.Models;

public record ObservationAcceptance(long Index, long Term, int ConsecutiveFailures, bool HistoricalOnly);
