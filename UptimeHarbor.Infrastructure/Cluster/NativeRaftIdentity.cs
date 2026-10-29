using DotNext.Net.Cluster;
using Microsoft.AspNetCore.Connections;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

public static class NativeRaftIdentity
{
    public static bool Validate(PeerCredential credential, string senderId, string messageType,
        string term, string stateVersion, PeerEnvelope envelope)
    {
        ClusterMemberId expected = ClusterMemberId.FromEndPoint(new UriEndPoint(credential.Address));
        if (!ClusterMemberId.TryParse(senderId, out ClusterMemberId sender) || sender != expected)
        {
            return false;
        }

        if (messageType is not ("RequestVote" or "PreVote" or "AppendEntries" or "InstallSnapshot"
            or "Resign" or "Metadata" or "Synchronize"))
        {
            return false;
        }

        if (credential.IsAdmission && messageType != "Metadata")
        {
            return false;
        }

        if (messageType is "RequestVote" or "PreVote" or "AppendEntries" or "InstallSnapshot")
        {
            if (!long.TryParse(term, out long parsed) || parsed < 0 || parsed != envelope.Term || stateVersion != "1")
            {
                return false;
            }
        }

        return true;
    }
}
