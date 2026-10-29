using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

public static class MonitoringDiagnostics
{
    public static MonitoringProgress Read(RevisionStateStore state, bool caughtUp, DateTimeOffset now)
    {
        RevisionImage? image = state.Current;
        if (!state.Available || image is null)
        {
            return new(image?.Index ?? 0, 0, 0, null, null, null, false, "RecoveryUnconfirmed");
        }

        MonitoringTarget[] targets = image.Values.Keys.Concat(image.Monitoring.Nodes.Keys).Distinct()
            .Select(id => MonitoringTargets.Find(image, id)).Where(target => target is not null && !target.Observers.IsEmpty)
            .Cast<MonitoringTarget>().ToArray();
        int open = targets.Count(target =>
        {
            MonitoringEvaluation? history = image.Monitoring.History.GetValueOrDefault(target.Id);
            DateTimeOffset? through = MonitoringReducer.HistoricalWatermark(image, target);
            return !through.HasValue || history is null || history.CheckGeneration != target.CheckGeneration || history.AtUtc < through;
        });
        DateTimeOffset? last = image.Monitoring.Live.Values.Select(item => (DateTimeOffset?)item.AtUtc).Min();
        DateTimeOffset? watermark = image.Monitoring.Streams.Values.Select(item => (DateTimeOffset?)item.WatermarkUtc).Min();
        double? age = null;
        if (last.HasValue)
        {
            age = Math.Max(0, (now - last.Value).TotalSeconds);
        }

        string reason = "HistoricalCatchUp";
        if (caughtUp && open == 0)
        {
            reason = "AwaitingFreshObservations";
            bool complete = targets.Length > 0 && targets.All(target =>
            {
                MonitoringEvaluation? item = image.Monitoring.Live.GetValueOrDefault(target.Id);
                return item is not null && item.CheckGeneration == target.CheckGeneration && item.Coverage == Domain.Cluster.CoverageState.Complete
                    && now - item.AtUtc < MonitoringTargets.Freshness(target);
            });
            if (complete)
            {
                reason = "Ready";
            }
        }

        return new(image.Index, image.Monitoring.Streams.Count, open, last, watermark, age,
            caughtUp && open == 0 && reason == "Ready", reason);
    }
}
