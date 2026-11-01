using System.Text.Json;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Cluster;

public class ClusterRevisionTransport(ClusterPeerRuntime runtime, RevisionDispatcher dispatcher,
    IPeerTrustSource trust, IHttpClientFactory clients, IOptions<ClusterOptions> configured) : IRevisionTransport
{
    public async Task<IReadOnlyList<CommandResult>> SendAsync(IReadOnlyList<RevisionCommand> commands, CancellationToken token)
    {
        ClusterOptions options = configured.Value;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(commands);
        RevisionBatchCodec.Decode(bytes, options);
        if (commands.Any(command => command.OriginNodeId != options.NodeId))
        {
            throw new InvalidDataException("LocalOriginMismatch");
        }

        Guid? leader = runtime.LeaderNodeId;
        if (leader == options.NodeId)
        {
            List<CommandResult> results = [];
            foreach (RevisionCommand command in commands)
            {
                results.Add(await dispatcher.AcceptAsync(command, options.NodeId, token));
            }

            return results;
        }

        IReadOnlyList<PeerCredential> peers = await trust.ListAsync(token);
        PeerCredential peer = peers.SingleOrDefault(peer => peer.NodeId == leader && peer.IsVoting)
            ?? throw new InvalidOperationException("LeaderUnavailable");
        HttpClient client = clients.CreateClient(PeerClientHandler.CLIENT_NAME);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(peer.Address, "/internal/cluster/v1/commands"));
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new("application/json");
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > options.BatchMaxBytes)
        {
            throw new HttpRequestException("CommandBatchUnavailable");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(token);
        byte[] received = await RevisionBatchCodec.ReadAsync(stream, options.BatchMaxBytes, token);
        IReadOnlyList<CommandResult> resultsReceived = JsonSerializer.Deserialize<List<CommandResult>>(received, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("DecisionBatchMissing");
        PendingReplicationPump.ValidateResults(commands, resultsReceived);
        return resultsReceived;
    }
}
