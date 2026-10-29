using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft.Membership;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Cluster;

public class ClusterPeerRuntime(IServiceProvider services, IOptions<ClusterOptions> configured,
    ClusterStateMachine machine, ClusterRuntimeState state, NodeReadiness readiness,
    SenderAuthorityGate gate, LeaderFailureWindow failureWindow, TimeProvider timeProvider,
    IPeerTrustSource trust, ILogger<ClusterPeerRuntime> logger) : BackgroundService
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private ServiceProvider? nodeServices;
    private IRaftHttpCluster? raft;
    private volatile RequestDelegate? protocol;
    private ClusterStorageLease? lease;
    private PeerReplayInbox? inbox;
    private volatile bool rpcOpen;
    private bool recoveryLogged;
    private readonly Guid bootId = Guid.NewGuid();
    public Guid? LeaderNodeId { get; private set; }
    public IRaftHttpCluster? Raft => raft;
    public bool CanAcceptCommands => rpcOpen && !machine.Failed && raft?.Leader?.IsRemote == false
        && readiness.CanWork(state);
    public SemaphoreSlim MembershipLock { get; } = new(1, 1);

    // Вызывать только под MembershipLock: stop/dispose ждёт тот же scope.
    public async ValueTask<long> ReadMembershipVersionAsync(CancellationToken cancellationToken)
    {
        if (nodeServices is null)
        {
            return -1;
        }

        IClusterConfigurationStorage storage = nodeServices.GetRequiredService<IClusterConfigurationStorage<UriEndPoint>>();
        var saved = await storage.LoadConfigurationAsync(cancellationToken);
        return saved.Version;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configured.Value.Enabled)
        {
            return;
        }

        MonotonicClockMonitor clock = new(timeProvider, readiness);
        if (Directory.Exists(configured.Value.StoragePath))
        {
            inbox = new(Path.Combine(configured.Value.StoragePath, "peer-inbox"),
                configured.Value.ReplayCapacity, timeProvider.GetUtcNow());
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                clock.Sample();
                gate.Tick();
                bool localHealthy = readiness.Get("Database").Status == "Ready"
                    && readiness.Get("Clock").Status == "Ready" && (!machine.Failed || raft is null);
                if (!localHealthy)
                {
                    await StopNodeAsync(stoppingToken);
                    continue;
                }

                await StartNodeAsync(stoppingToken);
                await RefreshStatusAsync(stoppingToken);
                ConsensusStatus? status = state.Consensus;
                if (status is not null)
                {
                    readiness.ConfirmRecovery(readiness.RecoveryVersion, state.Accepted, status);
                    failureWindow.ObserveTerm(status.Term, status.MembershipVersion, LeaderNodeId);
                    bool leaderReady = status.IsLeader && readiness.CanSend(state);
                    if (leaderReady)
                    {
                        failureWindow.ObserveAuthenticated(CreateHealth());
                    }

                    bool knownFailure = readiness.Get("Monitoring").Status == "Unavailable"
                        || readiness.Get("Telegram").Status == "Unavailable";
                    bool lostConsensus = status.LeadershipToken.IsCancellationRequested
                        || status.ConsensusToken.IsCancellationRequested;
                    // Ожидание accepted epoch/activation не является локальной поломкой:
                    // лидер должен успеть принять эти команды, оставаясь с закрытым sender.
                    if (status.IsLeader && (knownFailure || lostConsensus))
                    {
                        gate.Close();
                        await raft!.ResignAsync(stoppingToken);
                    }

                    if (failureWindow.ShouldRelinquish(status.VotingNodes, status.NodeId, leaderReady))
                    {
                        gate.Close();
                        await raft!.ResignAsync(stoppingToken);
                    }
                }
            }
        }
        finally
        {
            await StopNodeAsync(CancellationToken.None);
            inbox?.Dispose();
            inbox = null;
        }
    }

    public async Task StartNodeAsync(CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (raft is not null)
            {
                return;
            }

            ClusterOptions options = configured.Value;
            if (!options.Enabled || readiness.Get("Database").Status != "Ready" || readiness.Get("Clock").Status != "Ready")
            {
                return;
            }

            if (!await machine.RestoreAsync(options, cancellationToken))
            {
                if (!recoveryLogged)
                {
                    logger.LogWarning("Cluster recovery is unconfirmed for {NodeId}.", options.NodeId);
                    recoveryLogged = true;
                }

                return;
            }

            lease = new(options);
            // Отдельный контейнер не запускается как host: DotNext стартует только ниже.
            nodeServices = DotNextNodeServices.Create(services, machine, options, lease.ColdStart);
            IRaftHttpCluster candidate = nodeServices.GetRequiredService<IRaftHttpCluster>();
            await candidate.AuditTrail.InitializeAsync(cancellationToken);
            IClusterConfigurationStorage configurationStorage = nodeServices.GetRequiredService<IClusterConfigurationStorage<UriEndPoint>>();
            var saved = await configurationStorage.LoadConfigurationAsync(cancellationToken);
            WriteAheadLog wal = (WriteAheadLog)candidate.AuditTrail;
            IClusterRecovery recovery = services.GetRequiredService<IClusterRecovery>();
            bool replayConfirmed = await recovery.ConfirmLocalReplayAsync(wal.LastCommittedEntryIndex,
                wal.LastAppliedIndex, saved.Version, cancellationToken);
            if (!replayConfirmed)
            {
                await DisposeNodeAsync();
                return;
            }

            machine.ConfirmReplay(replayConfirmed);
            await ((IHostedService)candidate).StartAsync(cancellationToken);
            raft = candidate;
            protocol = DotNextNodeServices.CreateProtocol(nodeServices);
            readiness.Report("Storage", true, "Ready", TimeSpan.FromSeconds(10));
            rpcOpen = true;
            logger.LogInformation("Raft started for {NodeId}.", options.NodeId);
        }
        catch
        {
            rpcOpen = false;
            gate.Close();
            readiness.Report("Storage", false, "StorageUnavailable", TimeSpan.FromSeconds(10));
            await DisposeNodeAsync();
            throw;
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task RefreshStatusAsync(CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            await RefreshStatusCoreAsync(cancellationToken);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async Task RefreshStatusCoreAsync(CancellationToken cancellationToken)
    {
        if (raft is null || nodeServices is null)
        {
            return;
        }

        ClusterOptions options = configured.Value;
        IClusterConfigurationStorage<UriEndPoint> storage = nodeServices.GetRequiredService<IClusterConfigurationStorage<UriEndPoint>>();
        IClusterConfiguration<UriEndPoint> configuration = await storage.LoadConfigurationAsync(cancellationToken);
        var persisted = await ((IClusterConfigurationStorage)storage).LoadConfigurationAsync(cancellationToken);
        IReadOnlyList<PeerCredential> peers = await trust.ListAsync(cancellationToken);
        HashSet<Guid> voters = [];
        LeaderNodeId = null;
        foreach (UriEndPoint member in configuration.Members)
        {
            Guid id = options.NodeId;
            if (member.Uri.AbsoluteUri != options.PublicUri)
            {
                PeerCredential? peer = peers.FirstOrDefault(x => x.Address == member.Uri);
                if (peer is null)
                {
                    state.Publish(null);
                    gate.Close();
                    return;
                }

                id = peer.NodeId;
            }

            if (!voters.Add(id))
            {
                throw new InvalidDataException("A NodeId has multiple voting addresses.");
            }

            ClusterMemberId transportId = ClusterMemberId.FromEndPoint(member);
            if (raft.Leader?.Id == transportId)
            {
                LeaderNodeId = id;
            }
        }

        readiness.Report("Storage", true, "Ready", TimeSpan.FromSeconds(10));
        bool localLeader = raft.Leader?.IsRemote == false;
        IClusterRecovery recovery = services.GetRequiredService<IClusterRecovery>();
        WriteAheadLog wal = (WriteAheadLog)raft.AuditTrail;
        bool replayConfirmed = await recovery.ConfirmLocalReplayAsync(wal.LastCommittedEntryIndex, wal.LastAppliedIndex,
            persisted.Version, cancellationToken);
        machine.ConfirmReplay(replayConfirmed);
        state.Publish(new(options.ClusterId, options.Generation, options.NodeId, raft.AuditTrail.Term,
            persisted.Version, localLeader, raft.AuditTrail.LastCommittedEntryIndex, voters,
            raft.LeadershipToken, raft.ConsensusToken));
    }

    public async Task StopNodeAsync(CancellationToken cancellationToken)
    {
        rpcOpen = false;
        gate.Close();
        state.Publish(null);
        readiness.InvalidateRecovery();
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            await MembershipLock.WaitAsync(cancellationToken);
            try
            {
                state.Publish(null);
                if (raft is not null)
                {
                    await ((IHostedService)raft).StopAsync(cancellationToken);
                    logger.LogInformation("Raft stopped for {NodeId}.", configured.Value.NodeId);
                }

                await DisposeNodeAsync();
            }
            finally
            {
                MembershipLock.Release();
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async ValueTask DisposeNodeAsync()
    {
        raft = null;
        protocol = null;
        if (nodeServices is not null)
        {
            await nodeServices.DisposeAsync();
            nodeServices = null;
        }

        lease?.Dispose();
        lease = null;
    }

    public async Task ProcessRaftAsync(HttpContext context, PeerEnvelope envelope)
    {
        RequestDelegate? handler = protocol;
        if (!rpcOpen || handler is null || machine.Failed || readiness.Get("Database").Status != "Ready"
            || readiness.Get("Storage").Status != "Ready" || readiness.Get("Clock").Status != "Ready"
            || failureWindow.RejectLeaderRpc(envelope.FromNodeId, envelope.Term))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (context.Request.Headers["X-Raft-Message-Type"] == "Resign")
        {
            ConsensusStatus? current = state.Consensus;
            // Задержавшийся Resign прежнего term и жалоба на локально готового
            // главного не обходят readiness quorum. Большинство может изолировать RPC.
            if (current is null || envelope.Term != current.Term || readiness.CanSend(state))
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }
        }

        Stream original = context.Response.Body;
        await using MemoryStream body = new();
        context.Response.Body = body;
        try
        {
            await handler(context);
            if (!rpcOpen || machine.Failed || readiness.Get("Database").Status != "Ready"
                || readiness.Get("Storage").Status != "Ready" || readiness.Get("Clock").Status != "Ready")
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentLength = 0;
                return;
            }

            if (context.Response.StatusCode >= 400)
            {
                context.Response.ContentLength = 0;
                return;
            }

            body.Position = 0;
            await body.CopyToAsync(original, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = original;
        }
    }

    public bool AcceptNonce(PeerEnvelope envelope)
    {
        return inbox?.TryAccept(envelope.FromNodeId, envelope.RequestId, timeProvider.GetUtcNow()) == true;
    }

    public PeerHealthSnapshot CreateHealth()
    {
        Dictionary<string, HealthResult> components = new();
        foreach (string name in new[] { "Database", "Projection", "Monitoring", "Telegram", "Clock", "Storage" })
        {
            components.Add(name, readiness.Get(name));
        }

        ConsensusStatus? status = state.Consensus;
        bool ready = status?.IsLeader == true && readiness.CanSend(state);
        return new(configured.Value.NodeId, bootId, status?.Term ?? 0, LeaderNodeId,
            status?.MembershipVersion ?? -1, status?.CommittedIndex ?? 0, state.Accepted.AppliedIndex,
            ready, failureWindow.SecondsWithoutReady, components, failureWindow.ConfirmedPeers(),
            services.GetService<PendingReplicationPump>()?.Progress);
    }

    public async Task WriteHealthAsync(HttpContext context, PeerEnvelope envelope)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || context.Request.ContentLength > 0)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        await context.Response.WriteAsJsonAsync(CreateHealth(), context.RequestAborted);
    }
}
