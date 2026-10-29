using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

public class PeerClientHandler(IOptions<ClusterOptions> configured, IPeerTrustSource trust,
    TimeProvider timeProvider, IClusterStateView state) : DelegatingHandler
{
    public const string CLIENT_NAME = "UptimeHarbor.Peer";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ClusterOptions options = configured.Value;
        Uri uri = request.RequestUri ?? throw new InvalidOperationException("Peer URI is required.");
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Peer HTTPS is required.");
        }

        IReadOnlyList<PeerCredential> peers = await trust.ListAsync(cancellationToken);
        PeerCredential? target = peers.FirstOrDefault(peer => peer.Address.Authority == uri.Authority);
        if (target is null || target.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException("Peer trust is unavailable.");
        }

        long term = state.Consensus?.Term ?? 0;
        if (request.Headers.TryGetValues("X-Raft-Term", out IEnumerable<string>? terms))
        {
            string[] termValues = terms.Take(2).ToArray();
            if (termValues.Length != 1 || !long.TryParse(termValues[0], out term))
            {
                throw new InvalidOperationException("Native Raft term is invalid.");
            }
        }

        PeerEnvelope envelope = new(options.ClusterId, options.Generation, options.NodeId,
            target.NodeId, options.StorageGeneration, options.CredentialId, Guid.NewGuid(),
            timeProvider.GetUtcNow(), options.Compatibility, Guid.NewGuid(), term);
        request.Headers.Remove(PeerHeaders.ENVELOPE);
        request.Headers.Add(PeerHeaders.ENVELOPE, PeerHeaders.Encode(envelope));
        request.Headers.Remove("Cookie");
        request.Headers.Authorization = null;

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new HttpRequestException("Peer transport failed.");
        }

        if (!response.Headers.TryGetValues(PeerHeaders.ENVELOPE, out IEnumerable<string>? values))
        {
            response.Dispose();
            throw new HttpRequestException("Peer response identity is missing.");
        }

        string[] envelopes = values.Take(2).ToArray();
        if (envelopes.Length != 1)
        {
            response.Dispose();
            throw new HttpRequestException("Peer response identity is ambiguous.");
        }

        PeerEnvelope? reply = PeerHeaders.Decode(envelopes[0]);
        bool matched = reply is not null && reply.RequestId == envelope.RequestId && reply.Challenge == envelope.Challenge
            && reply.FromNodeId == target.NodeId && reply.ToNodeId == options.NodeId;
        if (!matched || reply!.ClusterId != options.ClusterId || reply.Generation != options.Generation
            || reply.Compatibility != options.Compatibility || reply.StorageGeneration != target.StorageGeneration
            || (timeProvider.GetUtcNow() - reply.IssuedAtUtc).Duration() > TimeSpan.FromSeconds(30))
        {
            response.Dispose();
            throw new HttpRequestException("Peer response identity is invalid.");
        }

        PeerCredential? credential = await trust.FindAsync(reply.CredentialId, cancellationToken);
        if (credential is null || credential.NodeId != target.NodeId || credential.Address != target.Address
            || credential.StorageGeneration != target.StorageGeneration || credential.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            response.Dispose();
            throw new HttpRequestException("Peer response trust is unavailable.");
        }

        return response;
    }
}
