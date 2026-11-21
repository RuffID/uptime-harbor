using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Пул ограничен удостоверенной парой leaf и, для legacy Direct, одной серверной цепочкой.</summary>
internal abstract class PeerBoundTransport<TLocal>(HttpRequestOptionsKey<TLocal> localCredential,
    IPeerTrustSource trust, TimeProvider time, PeerBoundTransport<TLocal>.HandlerFactory createHandler) : HttpMessageHandler
    where TLocal : class
{
    internal delegate ValueTask ConfirmConnection(HttpRequestMessage request, X509Certificate2 server,
        X509Certificate? client, CancellationToken token, PeerServerPin.ChainProof? chain = null);

    internal delegate HttpMessageHandler HandlerFactory(TLocal snapshot, bool pooled, ConfirmConnection confirm);

    private readonly ConditionalWeakTable<TLocal, SnapshotPools> transports = new();
    private static readonly HttpRequestOptionsKey<PoolSlot> FRESH_OBSERVATION = new("UptimeHarbor.FreshPeerObservation");

    protected abstract X509Certificate2 GetCertificate(TLocal snapshot);
    protected virtual bool IsUsable(TLocal snapshot) => true;
    protected virtual bool RequiresChainProof => false;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(localCredential, out TLocal? snapshot)
            || !request.Options.TryGetValue(PeerClientHandler.EXPECTED_SERVER,
                out (Guid NodeId, Guid StorageGeneration, Uri Address) target))
        {
            throw new HttpRequestException("Peer credential is unavailable.");
        }

        RequireLocal(snapshot);
        SnapshotPools pools = transports.GetValue(snapshot, item => new SnapshotPools(() =>
            new HttpMessageInvoker(createHandler(item, false, (message, server, client, token, chain) =>
                ObserveFreshAsync(item, message, server, client, chain, token)))));
        PoolSlot slot = pools.Targets.GetOrAdd(target, _ => new PoolSlot());
        if (!request.Options.TryGetValue(PeerClientHandler.CONNECTION_BINDING, out _) || slot.Pin is not { } pin)
        {
            request.Options.Set(FRESH_OBSERVATION, slot);
            return await pools.Fresh.Value.SendAsync(request, cancellationToken);
        }

        void Invalidate()
        {
            if (pools.Targets.TryUpdate(target, new PoolSlot(), slot))
            {
                slot.Dispose();
            }
        }

        await RequireCurrentPinAsync(pin, Invalidate, cancellationToken, recheckChain: true);
        string clientFingerprint = RequireLocal(snapshot);
        PeerClientHandler.ConfirmBinding(request, pin, clientFingerprint);
        request.Headers.ConnectionClose = false;
        HttpMessageInvoker transport = slot.GetTransport(() => new HttpMessageInvoker(createHandler(snapshot, true,
            (message, server, client, token, chain) =>
                ConfirmPinnedAsync(snapshot, pin, Invalidate, message, server, client, chain, token))));
        HttpResponseMessage response = await transport.SendAsync(request, cancellationToken);
        try
        {
            await RequireCurrentPinAsync(pin, Invalidate, cancellationToken, recheckChain: false);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async ValueTask ObserveFreshAsync(TLocal snapshot, HttpRequestMessage request, X509Certificate2 server,
        X509Certificate? client, PeerServerPin.ChainProof? chain, CancellationToken token)
    {
        RequirePresentedClient(snapshot, client);
        if (RequiresChainProof && chain is null)
        {
            throw new HttpRequestException("PeerServerChainMissing");
        }

        await PeerClientHandler.BindServerAsync(request, server, trust, time, token, client);
        RequireLocal(snapshot);
        if (request.Options.TryGetValue(FRESH_OBSERVATION, out PoolSlot? slot)
            && request.Options.TryGetValue(PeerClientHandler.PRESENTED_SERVER, out PeerServerPin.Observation? pin)
            && pin is not null && pin.Credential.IsVoting && !pin.Credential.IsAdmission)
        {
            slot.Observe(pin with { Chain = chain });
        }
    }

    private async ValueTask ConfirmPinnedAsync(TLocal snapshot, PeerServerPin.Observation pin, Action invalidate,
        HttpRequestMessage request, X509Certificate2 server, X509Certificate? client, PeerServerPin.ChainProof? chain,
        CancellationToken token)
    {
        string clientFingerprint = RequirePresentedClient(snapshot, client);
        if (!pin.Matches(server))
        {
            // Цепочка и имя уже проверены. Другой допустимый leaf требует отдельного вызова.
            PeerCredential? different = await PeerServerPin.ResolvePresentedAsync(request.RequestUri!, server,
                trust, time.GetUtcNow(), token);
            PeerCredential expected = pin.Credential;
            if (different is { IsVoting: true, IsAdmission: false } && different.NodeId == expected.NodeId
                && different.StorageGeneration == expected.StorageGeneration && different.Address == expected.Address)
            {
                invalidate();
            }

            throw new HttpRequestException("PeerServerPinChanged");
        }

        if (pin.Chain?.Digest != chain?.Digest)
        {
            invalidate();
            throw new HttpRequestException("PeerServerChainChanged");
        }

        await RequireCurrentPinAsync(pin, invalidate, token, recheckChain: false);
        RequireLocal(snapshot);
        PeerClientHandler.ConfirmBinding(request, pin, clientFingerprint);
    }

    private async ValueTask RequireCurrentPinAsync(PeerServerPin.Observation pin, Action invalidate, CancellationToken token,
        bool recheckChain)
    {
        token.ThrowIfCancellationRequested();
        if (recheckChain && pin.Chain is { } chain && !chain.Validate(time.GetUtcNow()))
        {
            invalidate();
            throw new HttpRequestException("PeerServerChainRejected");
        }

        PeerCredential? current = await trust.FindAsync(pin.Credential.CredentialId, token);
        token.ThrowIfCancellationRequested();
        if (current != pin.Credential || !pin.IsValidAt(time.GetUtcNow()))
        {
            invalidate();
            throw new HttpRequestException("PeerServerPinRejected");
        }
    }

    private string RequirePresentedClient(TLocal snapshot, X509Certificate? client)
    {
        string fingerprint = RequireLocal(snapshot);
        if (client?.GetCertHashString(HashAlgorithmName.SHA256) != fingerprint)
        {
            throw new HttpRequestException("PeerLocalCredentialChanged");
        }

        return fingerprint;
    }

    private string RequireLocal(TLocal snapshot)
    {
        RuntimeGenerationContext.Verify();
        X509Certificate2 certificate = GetCertificate(snapshot);
        DateTime now = time.GetUtcNow().UtcDateTime;
        if (!IsUsable(snapshot) || now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime())
        {
            throw new HttpRequestException("PeerLocalCredentialChanged");
        }

        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (KeyValuePair<TLocal, SnapshotPools> transport in transports)
            {
                if (transport.Value.Fresh.IsValueCreated)
                {
                    transport.Value.Fresh.Value.Dispose();
                }

                foreach (PoolSlot slot in transport.Value.Targets.Values)
                {
                    slot.Dispose();
                }
            }

            transports.Clear();
        }

        base.Dispose(disposing);
    }

    private class SnapshotPools(Func<HttpMessageInvoker> fresh)
    {
        public Lazy<HttpMessageInvoker> Fresh { get; } = new(fresh);
        public ConcurrentDictionary<(Guid NodeId, Guid StorageGeneration, Uri Address), PoolSlot> Targets { get; } = new();
    }

    private class PoolSlot : IDisposable
    {
        private readonly object sync = new();
        private PeerServerPin.Observation? pin;
        private HttpMessageInvoker? transport;
        private bool disposed;

        public PeerServerPin.Observation? Pin => Volatile.Read(ref pin);

        public void Observe(PeerServerPin.Observation observed)
        {
            Interlocked.CompareExchange(ref pin, observed, null);
        }

        public HttpMessageInvoker GetTransport(Func<HttpMessageInvoker> factory)
        {
            lock (sync)
            {
                if (disposed)
                {
                    throw new HttpRequestException("PeerServerPoolInvalidated");
                }

                transport ??= factory();
                return transport;
            }
        }

        public void Dispose()
        {
            HttpMessageInvoker? retired;
            lock (sync)
            {
                disposed = true;
                retired = transport;
                transport = null;
            }

            retired?.Dispose();
        }
    }
}
