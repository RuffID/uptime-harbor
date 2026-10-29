using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

public class SenderAuthorityGate(IClusterStateView state, NodeReadiness readiness,
    ISenderConsensus consensus, TimeProvider timeProvider) : IDisposable
{
    private readonly object sync = new();
    private SenderAuthorityKey? currentKey;
    private SenderAuthorityKey? authorityKey;
    private long roleSince;
    private long authoritySince;
    private long lastTick = timeProvider.GetTimestamp();
    private DateTimeOffset lastUtc = timeProvider.GetUtcNow();
    private long revision;
    private bool membershipChanging;
    private Guid suspendedEpoch;
    private CancellationTokenSource activeRequests = new();

    public void Tick()
    {
        lock (sync)
        {
            CheckTime();
            SenderAuthorityKey? key = ReadKey();
            if (key != currentKey)
            {
                CloseCore();
                currentKey = key;
                roleSince = timeProvider.GetTimestamp();
            }

            bool expired = authorityKey is not null && timeProvider.GetElapsedTime(authoritySince) >= TimeSpan.FromSeconds(10);
            if (!ConditionsHold() || expired)
            {
                CloseCore();
            }
        }
    }

    public void Close()
    {
        lock (sync)
        {
            CloseCore();
        }
    }

    public void SuspendMembership()
    {
        lock (sync)
        {
            membershipChanging = true;
            suspendedEpoch = state.Accepted.SenderEpoch;
            CloseCore();
        }
    }

    public bool ConfirmMembershipReconciled()
    {
        lock (sync)
        {
            AcceptedClusterState accepted = state.Accepted;
            if (accepted.MembershipChangePending || accepted.SenderEpoch == Guid.Empty
                || accepted.SenderEpoch == suspendedEpoch || !readiness.CanWork(state))
            {
                return false;
            }

            membershipChanging = false;
            currentKey = null;
            CloseCore();
            return true;
        }
    }

    public async ValueTask<bool> RenewAsync(CancellationToken cancellationToken)
    {
        SenderAuthorityKey key;
        long start;
        long expectedRevision;
        lock (sync)
        {
            Tick();
            if (currentKey is null || !ConditionsHold()
                || timeProvider.GetElapsedTime(roleSince) < TimeSpan.FromSeconds(12))
            {
                return false;
            }

            key = currentKey;
            start = timeProvider.GetTimestamp();
            expectedRevision = revision;
        }

        Guid round = Guid.NewGuid();
        AuthorityDecision? decision;
        try
        {
            decision = await consensus.RenewAsync(key, round, cancellationToken);
        }
        catch
        {
            Close();
            throw;
        }

        lock (sync)
        {
            Tick();
            bool matching = decision is not null && decision.AppliedIndex > 0 && decision.Key == key && decision.RoundId == round
                && decision.AppliedIndex <= state.Accepted.AppliedIndex;
            if (!matching || revision != expectedRevision || key != currentKey
                || !ConditionsHold() || timeProvider.GetElapsedTime(start) >= TimeSpan.FromSeconds(10))
            {
                CloseCore();
                return false;
            }

            authorityKey = key;
            authoritySince = start;
            return true;
        }
    }

    public async ValueTask<bool> ExecuteAttemptAsync(Guid attemptId,
        Func<CancellationToken, Task> transport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfEqual(attemptId, Guid.Empty);
        SenderAuthorityKey key;
        long start;
        long expectedRevision;
        lock (sync)
        {
            Tick();
            if (!HasAuthority())
            {
                return false;
            }

            key = authorityKey!;
            start = timeProvider.GetTimestamp();
            expectedRevision = revision;
        }

        DeliveryAttemptPermit? permit = await consensus.BeginAttemptAsync(key, attemptId, cancellationToken);
        CancellationTokenSource requestCancellation;
        lock (sync)
        {
            Tick();
            bool matching = permit is not null && permit.AppliedIndex > 0 && permit.Key == key && permit.AttemptId == attemptId
                && permit.AppliedIndex <= state.Accepted.AppliedIndex;
            if (!matching || revision != expectedRevision || !HasAuthority()
                || timeProvider.GetElapsedTime(start) > TimeSpan.FromSeconds(2))
            {
                return false;
            }

            ConsensusStatus status = state.Consensus!;
            requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                status.LeadershipToken, status.ConsensusToken, activeRequests.Token);
        }

        // Эта граница является началом in-flight попытки; атомарности с внешним API нет.
        using (requestCancellation)
        {
            await transport(requestCancellation.Token);
        }

        return true;
    }

    public bool CanStartTransport(SenderAuthorityKey key)
    {
        lock (sync)
        {
            Tick();
            return authorityKey == key && HasAuthority();
        }
    }

    private bool HasAuthority()
    {
        return authorityKey is not null && authorityKey == ReadKey() && ConditionsHold()
            && timeProvider.GetElapsedTime(authoritySince) < TimeSpan.FromSeconds(10);
    }

    private bool ConditionsHold()
    {
        ConsensusStatus? status = state.Consensus;
        if (membershipChanging || state.Accepted.MembershipChangePending
            || status is null || !status.IsLeader || status.VotingNodes.Count == 0
            || !status.VotingNodes.Contains(status.NodeId))
        {
            return false;
        }

        return !status.LeadershipToken.IsCancellationRequested && !status.ConsensusToken.IsCancellationRequested
            && readiness.CanSend(state) && state.Accepted.SenderEpoch != Guid.Empty;
    }

    private SenderAuthorityKey? ReadKey()
    {
        ConsensusStatus? status = state.Consensus;
        if (status is null || !status.IsLeader)
        {
            return null;
        }

        return new(status.ClusterId, status.Generation, status.MembershipVersion, status.Term,
            status.NodeId, state.Accepted.SenderEpoch);
    }

    private void CheckTime()
    {
        long now = timeProvider.GetTimestamp();
        DateTimeOffset utc = timeProvider.GetUtcNow();
        TimeSpan elapsed = timeProvider.GetElapsedTime(lastTick, now);
        TimeSpan drift = (utc - lastUtc - elapsed).Duration();
        if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromSeconds(5) || drift > TimeSpan.FromSeconds(2))
        {
            CloseCore();
            readiness.Report("Clock", false, "ClockUncertain", TimeSpan.FromSeconds(10));
        }

        lastTick = now;
        lastUtc = utc;
    }

    private void CloseCore()
    {
        authorityKey = null;
        activeRequests.Cancel();
        activeRequests.Dispose();
        activeRequests = new();
        revision++;
    }

    public void Dispose()
    {
        lock (sync)
        {
            activeRequests.Cancel();
            activeRequests.Dispose();
        }
    }
}
