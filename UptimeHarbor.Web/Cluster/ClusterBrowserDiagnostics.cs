using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Cluster;

public class ClusterBrowserDiagnostics(ClusterAdministrationAccess access, IOptions<ClusterOptions> options,
    IClusterStateView state, RevisionStateStore revisions, NodeReadiness readiness, ClusterPeerRuntime runtime,
    ClusterHealthWorker health, PendingReplicationPump replication, TimeProvider time)
{
    public ServiceResult<ClusterDiagnosticDto> Read()
    {
        ServiceError? error = access.GetError();
        if (error is not null)
        {
            return ServiceResult<ClusterDiagnosticDto>.Fail(error);
        }

        return ServiceResult<ClusterDiagnosticDto>.Ok(Project(options.Value.NodeId, options.Value.HealthFreshSeconds,
            state, revisions.Current!, readiness, runtime.LeaderNodeId, health.Peers, health.Connections,
            replication.Progress, time.GetUtcNow()));
    }

    public static ClusterDiagnosticDto Project(Guid nodeId, int healthFreshSeconds, IClusterStateView state,
        RevisionImage image, NodeReadiness readiness, Guid? leader,
        IReadOnlyDictionary<Guid, ObservedPeerHealth> peers, IReadOnlyDictionary<Guid, HealthResult> connections,
        ReplicationProgress progress, DateTimeOffset now)
    {
        ConsensusStatus? consensus = state.Consensus;
        bool currentConsensus = consensus is not null && !consensus.ConsensusToken.IsCancellationRequested;
        if (!currentConsensus)
        {
            leader = null;
        }
        string role = "Unknown";
        if (currentConsensus)
        {
            role = "Follower";
            if (consensus!.IsLeader && !consensus.LeadershipToken.IsCancellationRequested)
            {
                role = "Leader";
                leader = nodeId;
            }
        }

        string majority = "Unknown";
        if (currentConsensus && role == "Leader")
        {
            majority = "Confirmed";
        }
        else if (consensus?.ConsensusToken.IsCancellationRequested == true)
        {
            majority = "Unavailable";
        }

        HealthResult telegram = new("Unknown", "Unknown", null);
        bool senderReady = false;
        if (leader == nodeId)
        {
            telegram = readiness.Get("Telegram");
            senderReady = readiness.CanSend(state);
        }
        else if (consensus is not null && leader.HasValue && peers.TryGetValue(leader.Value, out ObservedPeerHealth? observed)
            && observed.Age >= TimeSpan.Zero && observed.Age < TimeSpan.FromSeconds(healthFreshSeconds)
            && observed.Health.Term == consensus?.Term && observed.Health.LeaderNodeId == leader
            && observed.Health.MembershipVersion == consensus.MembershipVersion)
        {
            HealthResult reported = observed.Health.Components.GetValueOrDefault("Telegram") ?? new("Unknown", "Unknown", null);
            TimeSpan age = observed.Age + (reported.Age ?? TimeSpan.Zero);
            if (reported.Age.HasValue && reported.Age.Value >= TimeSpan.Zero && age < TimeSpan.FromSeconds(90))
            {
                telegram = reported with { Age = age };
                senderReady = observed.Health.Ready;
            }
        }

        string sending = "Unknown";
        string reason = SafeTelegramReason(telegram.Reason);
        if (telegram.Status == "Unavailable")
        {
            sending = "Unavailable";
        }
        else if (telegram.Status == "Ready")
        {
            sending = "Unavailable";
            reason = "SenderNotReady";
            if (senderReady && currentConsensus)
            {
                sending = "Available";
                reason = "Ready";
            }
        }

        List<ClusterNodeDiagnosticDto> nodes = [];
        foreach (Guid id in image.Monitoring.Nodes.Keys.Order())
        {
            if (id == nodeId)
            {
                nodes.Add(new(id, "Local", 0, null, readiness.Get("Database").Status, role,
                    state.Accepted.AppliedIndex, Lag(consensus?.CommittedIndex, state.Accepted.AppliedIndex)));
                continue;
            }

            HealthResult connection = connections.GetValueOrDefault(id) ?? new("Unknown", "Unknown", null);
            ObservedPeerHealth? peer = peers.GetValueOrDefault(id);
            bool fresh = peer is not null && peer.Age >= TimeSpan.Zero && peer.Age < TimeSpan.FromSeconds(healthFreshSeconds);
            string database = "Unknown";
            string peerRole = "Unknown";
            long? applied = null;
            if (fresh)
            {
                HealthResult? db = peer!.Health.Components.GetValueOrDefault("Database");
                if (db?.Age is TimeSpan dbAge && dbAge >= TimeSpan.Zero
                    && dbAge + peer.Age < TimeSpan.FromSeconds(healthFreshSeconds))
                {
                    database = SafeStatus(db.Status);
                }

                peerRole = "Follower";
                if (peer.Health.LeaderNodeId == id)
                {
                    peerRole = "Leader";
                }

                applied = peer.Health.AppliedIndex;
            }

            DateTimeOffset? contact = null;
            if (peer is not null)
            {
                contact = now - peer.Age;
            }

            nodes.Add(new(id, SafeStatus(connection.Status), connection.Age?.TotalSeconds, contact,
                database, peerRole, applied, Lag(consensus?.CommittedIndex, applied)));
        }

        long backlog = image.Monitoring.Intents.Values.LongCount(intent => intent.State is
            UptimeHarbor.Domain.Cluster.DeliveryState.Planned or UptimeHarbor.Domain.Cluster.DeliveryState.RetryDue
            or UptimeHarbor.Domain.Cluster.DeliveryState.AttemptStarted);
        return new(nodeId, leader, role, majority,
            consensus?.VotingNodes.Count ?? 0, (consensus?.VotingNodes.Count ?? 0) / 2 + 1,
            state.Accepted.AppliedIndex, consensus?.CommittedIndex, Lag(consensus?.CommittedIndex, state.Accepted.AppliedIndex),
            progress.Pending, progress.AwaitingConfirmation, backlog, readiness.Get("Database").Status,
            sending, reason, telegram.Age?.TotalSeconds, now, nodes)
        {
            Term = consensus?.Term, MembershipVersion = consensus?.MembershipVersion,
            OperationsRevision = image.Operations.Revision, MaintenanceId = image.Operations.MaintenanceId,
            Conflicts = image.Conflicts.Values.LongCount(conflict => !conflict.ResolvedAtUtc.HasValue),
            TerminalDeliveryErrors = image.Monitoring.Intents.Values.LongCount(intent => intent.State is
                UptimeHarbor.Domain.Cluster.DeliveryState.Failed),
            LastSuccessfulSynchronizationAtUtc = progress.LastSuccessfulSynchronizationAtUtc,
            ProjectionAgeSeconds = readiness.Get("Projection").Age?.TotalSeconds
        };
    }

    private static long? Lag(long? committed, long? applied)
    {
        if (!committed.HasValue || !applied.HasValue)
        {
            return null;
        }

        return Math.Max(0, committed.Value - applied.Value);
    }

    private static string SafeStatus(string status)
    {
        if (status is "Ready" or "Unavailable")
        {
            return status;
        }

        return "Unknown";
    }

    private static string SafeTelegramReason(string reason)
    {
        if (reason is "Ready" or "Stale" or "TokenMissing" or "BotMismatch" or "TransportUnavailable" or "RateLimited")
        {
            return reason;
        }

        return "Unknown";
    }
}
