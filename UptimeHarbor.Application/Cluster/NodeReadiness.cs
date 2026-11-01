using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

public class NodeReadiness(TimeProvider timeProvider)
{
    private readonly object sync = new();
    private readonly Dictionary<string, (bool Healthy, string Reason, long At, TimeSpan Lifetime)> results = new();
    private long recoveryVersion;
    private long confirmedRecoveryVersion = -1;

    public long RecoveryVersion
    {
        get
        {
            lock (sync)
            {
                return recoveryVersion;
            }
        }
    }

    public void Report(string component, bool healthy, string reason, TimeSpan lifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        if (component is not ("Database" or "Projection" or "Monitoring" or "Telegram" or "Clock" or "Storage"))
        {
            throw new ArgumentException("Unknown health component.", nameof(component));
        }

        // Причины выбираются адаптерами из закрытого набора, без текста исключений.
        if (reason is not ("Ready" or "Unknown" or "DatabaseUnavailable" or "WorkerStalled"
            or "TokenMissing" or "BotMismatch" or "TransportUnavailable" or "RateLimited"
            or "ClockUncertain" or "StorageUnavailable" or "ProjectionUnavailable"))
        {
            throw new ArgumentException("Unknown health reason.", nameof(reason));
        }

        lock (sync)
        {
            if (!healthy && component is "Database" or "Storage")
            {
                recoveryVersion++;
                confirmedRecoveryVersion = -1;
            }

            results[component] = (healthy, reason, timeProvider.GetTimestamp(), lifetime);
        }
    }

    public HealthResult Get(string component)
    {
        lock (sync)
        {
            if (!results.TryGetValue(component, out var result))
            {
                return new("Unknown", "Unknown", null);
            }

            TimeSpan age = timeProvider.GetElapsedTime(result.At);
            if (age < TimeSpan.Zero || age >= result.Lifetime)
            {
                return new("Unknown", "Stale", age);
            }

            if (result.Healthy)
            {
                return new("Ready", result.Reason, age);
            }

            return new("Unavailable", result.Reason, age);
        }
    }

    public bool ConfirmRecovery(long expectedVersion, AcceptedClusterState accepted, ConsensusStatus consensus)
    {
        lock (sync)
        {
            bool matching = accepted.SnapshotConfirmed && accepted.AccountStateConfirmed && accepted.CaughtUp
                && accepted.AppliedIndex >= consensus.CommittedIndex
                && accepted.MembershipVersion == consensus.MembershipVersion;
            if (!matching || expectedVersion != recoveryVersion || Get("Database").Status != "Ready")
            {
                return false;
            }

            confirmedRecoveryVersion = recoveryVersion;
            return true;
        }
    }

    public bool CanWork(IClusterStateView state)
    {
        lock (sync)
        {
            ConsensusStatus? consensus = state.Consensus;
            AcceptedClusterState accepted = state.Accepted;
            if (consensus is null || confirmedRecoveryVersion != recoveryVersion)
            {
                return false;
            }

            bool acceptedReady = accepted.AccountStateConfirmed && accepted.SnapshotConfirmed
                && accepted.CaughtUp && accepted.Activated && accepted.ManuallyEnabled
                && accepted.AppliedIndex >= consensus.CommittedIndex
                && accepted.MembershipVersion == consensus.MembershipVersion;
            if (!acceptedReady)
            {
                return false;
            }

            return Get("Database").Status == "Ready" && Get("Storage").Status == "Ready"
                && Get("Clock").Status == "Ready";
        }
    }

    public bool CanSend(IClusterStateView state)
    {
        ConsensusStatus? status = state.Consensus;
        if (status is null || !status.IsLeader || status.LeadershipToken.IsCancellationRequested
            || status.ConsensusToken.IsCancellationRequested)
        {
            return false;
        }

        return CanWork(state) && Get("Monitoring").Status == "Ready"
            && Get("Telegram").Status == "Ready" && state.Accepted.DeliveryStateConfirmed
            && state.Accepted.SenderEpoch != Guid.Empty;
    }

    public void InvalidateRecovery()
    {
        lock (sync)
        {
            recoveryVersion++;
            confirmedRecoveryVersion = -1;
        }
    }
}
