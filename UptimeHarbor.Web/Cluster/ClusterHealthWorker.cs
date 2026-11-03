using System.Text.Json;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Cluster;

public class ClusterHealthWorker(IOptions<ClusterOptions> options, ClusterDatabaseProbe database,
    TelegramHealthProbe telegram, IPeerTrustSource trust,
    IHttpClientFactory clients, LeaderFailureWindow failureWindow,
    TimeProvider timeProvider, PeerObservationCapture? observations = null,
    ILogger<ClusterHealthWorker>? logger = null) : UptimeHarbor.Infrastructure.Installation.RestartableWorker
{
    private readonly Dictionary<Guid, (HealthResult Result, long At)> connections = new();
    private readonly object sync = new();
    private readonly Dictionary<Guid, (PeerHealthSnapshot Health, long At)> peers = new();

    public IReadOnlyDictionary<Guid, ObservedPeerHealth> Peers
    {
        get
        {
            lock (sync)
            {
                return peers.ToDictionary(entry => entry.Key, entry =>
                    new ObservedPeerHealth(entry.Value.Health, timeProvider.GetElapsedTime(entry.Value.At)));
            }
        }
    }

    public IReadOnlyDictionary<Guid, HealthResult> Connections
    {
        get
        {
            lock (sync)
            {
                Dictionary<Guid, HealthResult> snapshot = new();
                foreach (var entry in connections)
                {
                    TimeSpan age = timeProvider.GetElapsedTime(entry.Value.At);
                    HealthResult result = entry.Value.Result with { Age = age };
                    if (age >= TimeSpan.FromSeconds(options.Value.HealthFreshSeconds))
                    {
                        result = new("Unknown", "Stale", age);
                    }

                    snapshot.Add(entry.Key, result);
                }

                return snapshot;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(options.Value.HeartbeatSeconds), timeProvider);
        do
        {
            await database.ProbeAsync(stoppingToken);
            await telegram.ProbeAsync(stoppingToken);
            IReadOnlyList<PeerCredential> peers = await trust.ListAsync(stoppingToken);
            await Parallel.ForEachAsync(peers.Where(peer => peer.NodeId != options.Value.NodeId),
                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = stoppingToken },
                async (peer, cancellation) => await PollPeerAsync(peer, cancellation));
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task PollPeerAsync(PeerCredential peer, CancellationToken cancellationToken)
    {
        long start = timeProvider.GetTimestamp();
        PeerHealthSnapshot? observed = null;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            HttpClient client = clients.CreateClient(PeerClientHandler.CLIENT_NAME);
            using HttpResponseMessage response = await client.GetAsync(new Uri(peer.Address, "/internal/cluster/v1/health"),
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > options.Value.HealthMaxBytes)
            {
                RecordConnection(peer.NodeId, new("Unavailable", "PeerUnavailable", TimeSpan.Zero));
                return;
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
            byte[] bytes = new byte[options.Value.HealthMaxBytes + 1];
            int length = 0;
            while (length < bytes.Length)
            {
                int read = await body.ReadAsync(bytes.AsMemory(length), timeout.Token);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            if (length > options.Value.HealthMaxBytes || timeProvider.GetElapsedTime(start) >= TimeSpan.FromSeconds(3))
            {
                RecordConnection(peer.NodeId, new("Unknown", "PeerResponseInvalid", TimeSpan.Zero));
                return;
            }

            PeerHealthSnapshot? health = JsonSerializer.Deserialize<PeerHealthSnapshot>(bytes.AsSpan(0, length),
                JsonSerializerOptions.Web);
            if (health is null || health.NodeId != peer.NodeId || health.BootId == Guid.Empty)
            {
                RecordConnection(peer.NodeId, new("Unknown", "PeerResponseInvalid", TimeSpan.Zero));
                return;
            }

            UptimeHarbor.Application.Installation.RuntimeGenerationContext.Verify();
            failureWindow.ObserveAuthenticated(health);
            observed = health;
            lock (sync)
            {
                peers[peer.NodeId] = (health, timeProvider.GetTimestamp());
            }

            RecordConnection(peer.NodeId, new("Ready", "PeerReachable", TimeSpan.Zero));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            // Timeout не доказывает неисправность удалённой БД или Telegram.
            RecordConnection(peer.NodeId, new("Unknown", "PeerUnavailable", TimeSpan.Zero));
        }
        finally
        {
            if (observations is not null && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await observations.CaptureAsync(peer.NodeId, observed,
                        (long)timeProvider.GetElapsedTime(start).TotalMilliseconds, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    logger?.LogWarning("Peer observation capture failed for {NodeId}.", peer.NodeId);
                }
            }
        }
    }

    private void RecordConnection(Guid node, HealthResult result)
    {
        UptimeHarbor.Application.Installation.RuntimeGenerationContext.Verify();
        lock (sync)
        {
            connections[node] = (result, timeProvider.GetTimestamp());
        }
    }
}
