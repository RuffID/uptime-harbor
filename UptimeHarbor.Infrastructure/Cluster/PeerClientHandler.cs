using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster;

public class PeerClientHandler(IOptions<ClusterOptions> configured, IPeerTrustSource trust,
    TimeProvider timeProvider, IClusterStateView state, IOptions<InstallationOptions>? installation = null,
    DynamicPeerTls? tls = null) : DelegatingHandler
{
    public const string CLIENT_NAME = "UptimeHarbor.Peer";
    internal static readonly HttpRequestOptionsKey<(Guid NodeId, Guid StorageGeneration, Uri Address)> EXPECTED_SERVER =
        new("UptimeHarbor.ExpectedPeerServer");
    internal static readonly HttpRequestOptionsKey<PeerServerPin.Observation?> PRESENTED_SERVER =
        new("UptimeHarbor.PresentedPeerServer");
    public static readonly HttpRequestOptionsKey<PeerConnectionBinding> CONNECTION_BINDING = new("UptimeHarbor.PeerConnectionBinding");
    internal static readonly HttpRequestOptionsKey<DynamicPeerTls.Snapshot> LOCAL_CREDENTIAL =
        new("UptimeHarbor.LocalPeerCredential");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ClusterOptions options = configured.Value;
        bool managed = installation?.Value.Setup == true;
        Uri uri = request.RequestUri ?? throw new InvalidOperationException("Peer URI is required.");
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Peer HTTPS is required.");
        }

        request.Options.TryGetValue(CONNECTION_BINDING, out PeerConnectionBinding? binding);
        if (binding is not null && (options.TlsBoundary != "Direct" || binding.Address.Authority != uri.Authority
            || !managed && binding.ExpectedLocalSnapshot is not null))
        {
            throw new HttpRequestException("Peer connection binding is unavailable.");
        }

        (Guid NodeId, Guid StorageGeneration, Uri Address) target;
        if (binding is not null)
        {
            target = (binding.NodeId, binding.StorageGeneration, binding.Address);
        }
        else
        {
            IReadOnlyList<PeerCredential> peers = await trust.ListAsync(cancellationToken);
            PeerCredential? configuredTarget = peers.FirstOrDefault(peer => peer.Address.Authority == uri.Authority);
            if (configuredTarget is null || configuredTarget.ExpiresAtUtc <= timeProvider.GetUtcNow())
            {
                throw new InvalidOperationException("Peer trust is unavailable.");
            }

            target = (configuredTarget.NodeId, configuredTarget.StorageGeneration, configuredTarget.Address);
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

        DynamicPeerTls.Snapshot? local = null;
        if (managed && tls is not null)
        {
            local = tls.Capture() ?? throw new HttpRequestException("Peer credential is unavailable.");
            if (local.Quarantined && !new PathString(uri.AbsolutePath).StartsWithSegments("/internal/raft"))
            {
                throw new HttpRequestException("Peer credential is limited to native quarantine.");
            }

            if (binding?.ExpectedLocalSnapshot is { } expected && !ReferenceEquals(expected, local))
            {
                throw new HttpRequestException("Peer local credential changed before dispatch.");
            }

            request.Options.Set(LOCAL_CREDENTIAL, local);
        }

        PeerEnvelope envelope = new(options.ClusterId, options.Generation, options.NodeId,
            target.NodeId, options.StorageGeneration, local?.CredentialId ?? options.CredentialId, Guid.NewGuid(),
            timeProvider.GetUtcNow(), options.Compatibility, Guid.NewGuid(), term);
        request.Headers.Remove(PeerHeaders.ENVELOPE);
        request.Headers.Add(PeerHeaders.ENVELOPE, PeerHeaders.Encode(envelope));
        request.Headers.Remove("Cookie");
        request.Headers.Authorization = null;
        if (managed || binding is not null)
        {
            // Без фиксированного pin каждый запрос удостоверяется на новом соединении.
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Headers.ConnectionClose = true;
            request.Options.Set(EXPECTED_SERVER, target);
            request.Options.Set(PRESENTED_SERVER, null);
        }

        HttpResponseMessage response;
        try
        {
            RuntimeGenerationContext.Verify();
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

        try
        {
            request.Options.TryGetValue(PRESENTED_SERVER, out PeerServerPin.Observation? presented);
            if ((managed || binding is not null) && (presented is null || presented.Credential.CredentialId != reply.CredentialId))
            {
                throw new HttpRequestException("Peer response TLS identity is invalid.");
            }

            PeerCredential? credential = await trust.FindAsync(reply.CredentialId, cancellationToken);
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (credential is null || credential.NodeId != target.NodeId || credential.Address != target.Address
                || credential.StorageGeneration != target.StorageGeneration || credential.ExpiresAtUtc <= now
                || presented is not null && (presented.Credential != credential || !presented.IsValidAt(now)))
            {
                throw new HttpRequestException("Peer response trust is unavailable.");
            }

            RuntimeGenerationContext.Verify();
            if (local is not null && (tls?.IsUsable(local) != true
                || now.UtcDateTime < local.Certificate.NotBefore.ToUniversalTime()
                || now.UtcDateTime >= local.Certificate.NotAfter.ToUniversalTime()))
            {
                throw new HttpRequestException("Peer response local credential is unavailable.");
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public static async ValueTask BindServerAsync(HttpRequestMessage request, X509Certificate2 server,
        IPeerTrustSource trust, TimeProvider time, CancellationToken token, X509Certificate? client = null)
    {
        if (request.RequestUri is not Uri address
            || !request.Options.TryGetValue(EXPECTED_SERVER, out (Guid NodeId, Guid StorageGeneration, Uri Address) expected))
        {
            throw new HttpRequestException("PeerServerPinMissing");
        }

        PeerCredential? presented = await PeerServerPin.ResolvePresentedAsync(address, server, trust, time.GetUtcNow(), token);
        if (presented is null || presented.NodeId != expected.NodeId || presented.StorageGeneration != expected.StorageGeneration
            || presented.Address != expected.Address || presented.ExpiresAtUtc <= time.GetUtcNow())
        {
            throw new HttpRequestException("PeerServerPinRejected");
        }

        PeerServerPin.Observation observation = new(presented, server.NotBefore.ToUniversalTime(),
            server.NotAfter.ToUniversalTime());
        ConfirmBinding(request, observation, client?.GetCertHashString(HashAlgorithmName.SHA256));
    }

    internal static void ConfirmBinding(HttpRequestMessage request, PeerServerPin.Observation observation,
        string? clientFingerprint)
    {
        if (request.Options.TryGetValue(LOCAL_CREDENTIAL, out DynamicPeerTls.Snapshot? local)
            && clientFingerprint != local.Fingerprint)
        {
            throw new HttpRequestException("PeerLocalCredentialChanged");
        }

        if (request.Options.TryGetValue(CONNECTION_BINDING, out PeerConnectionBinding? binding))
        {
            if (!string.Equals(clientFingerprint, binding.ClientFingerprint, StringComparison.OrdinalIgnoreCase)
                || !observation.Credential.IsVoting || observation.Credential.IsAdmission)
            {
                throw new HttpRequestException("PeerConnectionIdentityRejected");
            }

            binding.ConfirmServer(observation.Credential);
        }

        request.Options.Set(PRESENTED_SERVER, observation);
    }
}
