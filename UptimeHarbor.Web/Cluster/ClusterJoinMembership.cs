using DotNext.Net.Cluster.Consensus.Raft.Membership;
using Microsoft.AspNetCore.Connections;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Первый пустой join получает точный native состав из уже проверенного checkpoint.</summary>
public static class ClusterJoinMembership
{
    public static async Task PrepareAsync(IClusterConfigurationStorage<UriEndPoint> membership,
        RevisionImage image, CancellationToken token)
    {
        IClusterConfiguration<UriEndPoint> initial = await membership.LoadConfigurationAsync(token);
        if (initial.Members.Count != 0)
        {
            throw new InvalidDataException("JoinMembershipNotEmpty");
        }

        foreach (OperationalNode voter in image.Operations.Nodes.Values.Where(node => node.Voting && !node.Removed))
        {
            initial = initial.Add(new UriEndPoint(new Uri(voter.Address)));
        }

        if (initial.Members.Count == 0)
        {
            throw new InvalidDataException("JoinVotersMissing");
        }

        await ((IClusterConfigurationStorage)membership).SaveConfigurationAsync(initial, image.Operations.MembershipVersion, token);
    }
}
