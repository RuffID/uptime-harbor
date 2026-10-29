namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Безопасная диагностика; возраст принятой оценки не является новым accepted переходом.</summary>
public record MonitoringProgress(long AppliedIndex, int Streams, int HistoricalAssemblies,
    DateTimeOffset? LastEvaluationUtc, DateTimeOffset? OldestWatermarkUtc,
    double? EvaluationAgeSeconds, bool CaughtUp, string Reason);
