using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Tests.Application.Cluster;

internal class AuthorityFixture : IClusterStateView, ISenderConsensus
{
    public ControlledTime Time { get; } = new();
    public CancellationTokenSource Majority { get; } = new();
    public NodeReadiness Readiness { get; }
    public SenderAuthorityGate Gate { get; }
    public ConsensusStatus? Consensus { get; set; }
    public AcceptedClusterState Accepted { get; set; }
    public bool AcceptRenewal { get; set; } = true;
    public bool AcceptAttempt { get; set; } = true;
    public double RenewalDelay { get; set; }
    public double AttemptDelay { get; set; }
    public int Renewals { get; private set; }
    public int Attempts { get; private set; }

    public AuthorityFixture()
    {
        Guid node = Guid.NewGuid();
        Consensus = new(Guid.NewGuid(), 1, node, 1, 1, true, 10,
            new HashSet<Guid> { node }, CancellationToken.None, Majority.Token);
        Accepted = new(true, true, true, true, true, 10, 1, Guid.NewGuid(), true);
        Readiness = new(Time);
        foreach (string component in new[] { "Database", "Monitoring", "Telegram", "Clock", "Storage" })
        {
            Readiness.Report(component, true, "Ready", TimeSpan.FromDays(1));
        }

        Readiness.ConfirmRecovery(Readiness.RecoveryVersion, Accepted, Consensus);
        Gate = new(this, Readiness, this, Time);
        Gate.Tick();
    }

    public void TickSeconds(int seconds)
    {
        foreach (int _ in Enumerable.Range(0, seconds))
        {
            Time.Advance(1);
            Gate.Tick();
        }
    }

    public ValueTask<AuthorityDecision?> RenewAsync(SenderAuthorityKey key, Guid roundId, CancellationToken cancellationToken)
    {
        Renewals++;
        Time.Advance(RenewalDelay);
        if (!AcceptRenewal)
        {
            return ValueTask.FromResult<AuthorityDecision?>(null);
        }

        return ValueTask.FromResult<AuthorityDecision?>(new(key, roundId, Accepted.AppliedIndex));
    }

    public ValueTask<DeliveryAttemptPermit?> BeginAttemptAsync(SenderAuthorityKey key, Guid attemptId, CancellationToken cancellationToken)
    {
        Attempts++;
        Time.Advance(AttemptDelay);
        if (!AcceptAttempt)
        {
            return ValueTask.FromResult<DeliveryAttemptPermit?>(null);
        }

        return ValueTask.FromResult<DeliveryAttemptPermit?>(new(key, attemptId, Accepted.AppliedIndex));
    }
}
