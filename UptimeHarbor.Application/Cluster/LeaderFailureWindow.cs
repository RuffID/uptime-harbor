using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Только quorum readiness evidence; выборы и term остаются ответственностью DotNext.</summary>
public class LeaderFailureWindow(TimeProvider timeProvider)
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, (PeerHealthSnapshot Health, long At)> witnesses = new();
    private long lastReady = timeProvider.GetTimestamp();
    private long term = -1;
    private long membership = -1;
    private Guid? leader;
    private Guid? isolatedLeader;
    private long isolatedTerm = -1;

    public double SecondsWithoutReady
    {
        get
        {
            lock (sync)
            {
                return timeProvider.GetElapsedTime(lastReady).TotalSeconds;
            }
        }
    }

    public void ObserveTerm(long currentTerm, long membershipVersion, Guid? leaderId)
    {
        lock (sync)
        {
            if (term != currentTerm || membership != membershipVersion || leader != leaderId)
            {
                term = currentTerm;
                membership = membershipVersion;
                leader = leaderId;
                lastReady = timeProvider.GetTimestamp();
                witnesses.Clear();
            }
        }
    }

    public void ObserveAuthenticated(PeerHealthSnapshot health)
    {
        lock (sync)
        {
            if (health.Term != term || health.MembershipVersion != membership || health.LeaderNodeId != leader)
            {
                return;
            }

            witnesses[health.NodeId] = (health, timeProvider.GetTimestamp());
            if (health.NodeId == leader && health.Ready)
            {
                lastReady = timeProvider.GetTimestamp();
            }
        }
    }

    public bool ShouldRelinquish(IReadOnlySet<Guid> voters, Guid localNode, bool localLeaderReady)
    {
        lock (sync)
        {
            if (leader is null || voters.Count == 0 || !voters.Contains(localNode)
                || localLeaderReady || SecondsWithoutReady < 30)
            {
                return false;
            }

            HashSet<Guid> quorum = [localNode];
            foreach (var witness in witnesses)
            {
                if (witness.Key == localNode || !voters.Contains(witness.Key))
                {
                    continue;
                }

                bool fresh = timeProvider.GetElapsedTime(witness.Value.At) < TimeSpan.FromSeconds(10);
                PeerHealthSnapshot health = witness.Value.Health;
                if (fresh && health.SecondsWithoutReadyLeader >= 30
                    && health.ConfirmedPeers?.Contains(localNode) == true)
                {
                    quorum.Add(witness.Key);
                }
            }

            // Каждый участник подтверждает прямую свежую связь с тем же большинством.
            // Пересказ чужого наблюдения и односторонняя HTTP-связь голосом не считаются.
            foreach (Guid node in quorum.ToArray())
            {
                if (node != localNode && !quorum.All(peer => peer == node
                    || witnesses[node].Health.ConfirmedPeers!.Contains(peer)))
                {
                    quorum.Remove(node);
                }
            }

            if (quorum.Count >= voters.Count / 2 + 1)
            {
                isolatedLeader = leader;
                isolatedTerm = term;
                return true;
            }

            return false;
        }
    }

    public bool RejectLeaderRpc(Guid sender, long senderTerm)
    {
        lock (sync)
        {
            return sender == isolatedLeader && senderTerm <= isolatedTerm;
        }
    }

    public IReadOnlySet<Guid> ConfirmedPeers()
    {
        lock (sync)
        {
            HashSet<Guid> peers = [];
            foreach (var witness in witnesses)
            {
                if (timeProvider.GetElapsedTime(witness.Value.At) < TimeSpan.FromSeconds(10))
                {
                    peers.Add(witness.Key);
                }
            }

            return peers;
        }
    }
}
