using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft.Membership;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using UptimeHarbor.Web.Cluster;
using Xunit;
using static UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting.VoterContinuationPerimeterTests;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

internal sealed class ManagedVoterContinuationStartupFixture : IAsyncDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("uh-continuation-start-").FullName;
    private readonly VoterContinuationHttpAuthority authority = new(administrator: true, signing: true);
    private readonly InstallationState leader = new();
    private readonly RevisionStateStore revisions = new();
    private readonly ClusterRaftHandle handle = new();
    private readonly StartupLog log = new();
    private readonly Admission admission = new();
    private InstallationOptions? localOptions;
    private string? storagePath;
    private ServiceProvider? leaderServices;
    private ServiceProvider? localServices;
    private InstallationLease? lease;
    private SenderAuthorityGate? gate;
    private ClusterRuntimeState? state;
    private ClusterPeerRuntime? runtime;
    private RevisionSnapshotStore? snapshots;
    private RevisionImage? replayImage;
    private ClusterOptions? clusterOptions;
    private ClusterStateMachine? machine;

    internal VoterContinuationHttpAuthority Authority => authority;
    internal InstallationState Installation { get; } = new();
    internal DynamicPeerTls Tls { get; private set; } = null!;
    internal ContinuationHandler Handler { get; private set; } = null!;
    internal IReadOnlyList<Guid> Diagnostics => log.Requests;
    internal Guid ReplayOperation { get; private set; }
    internal Guid PreviousCredential { get; private set; }
    internal string Reference { get; private set; } = null!;
    internal string IntentPath { get; private set; } = null!;

    internal static async Task<ManagedVoterContinuationStartupFixture> OpenAsync(CancellationToken token)
    {
        ManagedVoterContinuationStartupFixture fixture = new();
        try
        {
            await fixture.PrepareAsync(token);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private async Task PrepareAsync(CancellationToken token)
    {
        leader.Publish(authority.Profile);
        leader.ConfirmCommonOpen(authority.Profile);
        leaderServices = Provider(leader, authority);
        InstallationOptions setup = Setup();
        localOptions = setup;
        setup.CredentialContinuationOrigin = setup.BrowserOrigin;
        setup.CredentialContinuationPeerAddress = authority.Profile.PeerAddress!;
        setup.StateDirectory = Path.Combine(root, "state");
        setup.SecretDirectory = Path.Combine(root, "secret");
        setup.KeyDirectory = Path.Combine(root, "keys");
        RuntimeWriteGate writes = new();
        writes.Open(authority.Profile.RuntimeGeneration);
        PeerBoundaryMiddleware middleware = new(context => Controller(context).ExchangeAsync(),
            Options.Create(authority.Options), Options.Create(setup), leader);
        Handler = new(leaderServices, middleware, writes);
        var local = Options.Create(setup);
        RevisionImage image = authority.ReadImage();
        OperationalNode node = image.Operations.Nodes[authority.VoterId];
        var issuer = Assert.Single(image.OnboardingContracts!.Issuers!).Value;
        image = image with { OnboardingContracts = image.OnboardingContracts with
        {
            Genesis = new(1, Guid.NewGuid(), Guid.NewGuid(), image.ClusterId, image.Generation, issuer.NodeId,
                issuer.StorageGeneration, authority.Profile.PeerAddress!, issuer.SpkiDigest, issuer.CertificateDigest, image.Digest!, null)
        } };
        ReplayOperation = Assert.Single(image.Results).Key;
        PreviousCredential = Assert.Single(image.Operations.Credentials).Key;
        ClusterOptions options = new()
        {
            Mode = "Restart", ClusterId = image.ClusterId, Generation = image.Generation, NodeId = node.NodeId,
            StorageGeneration = node.StorageGeneration, PublicUri = node.Address, ProtocolMajor = 3,
            CommandSchema = 3, SnapshotFormat = 3, SqlSchema = 3, CredentialId = PreviousCredential,
            StoragePath = Path.Combine(root, "storage")
        };
        var cluster = Options.Create(options);
        clusterOptions = options;
        storagePath = options.StoragePath;
        lease = new(setup, options.StoragePath);
        LocalSecretStore secrets = new(local, lease);
        Guid attempt = Guid.NewGuid();
        using ECDsa key = authority.OpenPreviousKey();
        byte[] pem = Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem());
        string previousReference;
        try
        {
            previousReference = secrets.Write(attempt, "peer", pem);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }

        var profile = new LocalInstallationProfile(lease.InstallIdentity, "Bootstrap", InstallationPhase.ClusterQuarantine,
            1, 1, 1, lease.BootId, authority.GetUtcNow(), AttemptId: attempt, NodeId: node.NodeId,
            StorageGeneration: node.StorageGeneration, PeerAddress: node.Address, KeyReference: previousReference,
            ClusterId: image.ClusterId, ClusterGeneration: image.Generation, CredentialId: PreviousCredential) { ManagedRestart = true };
        Installation.Publish(profile);
        Reference = LocalVoterContinuationInstallation.Reference(lease.InstallIdentity, authority.ClientIntent().Request);
        IntentPath = Path.Combine(setup.SecretDirectory, "voter-renewal", Reference, "intent");
        replayImage = image;
        snapshots = new(cluster);
        PublishedRevisionSnapshot checkpoint = await snapshots.PublishAsync(image, token);
        admission.Proof = new(image.ClusterId, image.Generation, node.NodeId, node.StorageGeneration,
            false, false, checkpoint.Manifest.SnapshotId, checkpoint.Digest, image.Index, image.Operations.MembershipVersion)
        {
            Operations = image.Operations,
            Proof = new ManagedRestartProof(lease.InstallIdentity, lease.BootId, lease.BootChallenge, new string('A', 64))
        };

        Tls = new(Installation, revisions, authority);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<InstallationState>(Installation);
        services.AddSingleton<IHttpMessageHandlerFactory, NoNetwork>();
        services.AddSingleton<IClusterRecovery>(provider =>
        {
            Projection projection = new(() => replayImage!);
            return new RevisionClusterRecovery(admission, revisions, new(revisions, new(), projection), projection,
                snapshots, provider.GetRequiredService<IServiceScopeFactory>(), cluster, time: authority);
        });
        EnrollmentEndpoints endpoints = new(new SetupPeerAddressPolicy(setup));
        services.AddSingleton(_ => new EnrollmentTransport(Handler, endpoints));
        services.AddSingleton(provider => new VoterContinuationClient(provider.GetRequiredService<EnrollmentTransport>(), endpoints, local, authority));
        services.AddScoped(_ => new LocalVoterCredentialFiles(local, secrets));
        services.AddScoped(provider => new LocalVoterContinuationInstallation(provider.GetRequiredService<LocalVoterCredentialFiles>(),
            secrets, lease, Installation, null!, authority, NullLogger<LocalVoterContinuationInstallation>.Instance));
        services.AddScoped(provider => new ManagedVoterContinuationInstallation(provider.GetRequiredService<LocalVoterCredentialFiles>(),
            provider.GetRequiredService<LocalVoterContinuationInstallation>(), Tls, authority, NullLogger<ManagedVoterContinuationInstallation>.Instance));
        services.AddScoped(provider => new ManagedVoterContinuationStartup(lease, local, secrets,
            provider.GetRequiredService<LocalVoterCredentialFiles>(), provider.GetRequiredService<LocalVoterContinuationInstallation>(),
            provider.GetRequiredService<ManagedVoterContinuationInstallation>(), provider.GetRequiredService<VoterContinuationClient>(),
            endpoints, authority));
        services.AddSingleton(Tls);
        localServices = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        IClusterRecovery recovery = localServices.GetRequiredService<IClusterRecovery>();
        var readiness = new NodeReadiness(authority);
        readiness.Report("Database", true, "Ready", TimeSpan.FromMinutes(10));
        readiness.Report("Clock", true, "Ready", TimeSpan.FromMinutes(10));
        machine = new(recovery, readiness);
        state = new(recovery, revisions);
        gate = new(state, readiness, null!, authority);
        runtime = new NoNative(localServices, cluster, machine, state, readiness, gate, authority, log, handle, revisions);

        await PrepareNativeFilesAsync(options, image, machine, token);
    }

    private async Task PrepareNativeFilesAsync(ClusterOptions options, RevisionImage image, ClusterStateMachine machine, CancellationToken token)
    {
        var manifest = new ClusterDiskManifest(options.ClusterId, options.Generation, options.NodeId, options.StorageGeneration,
            options.PublicUri, Guid.NewGuid());
        File.WriteAllBytes(Path.Combine(options.StoragePath, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
        File.WriteAllBytes(Path.Combine(options.StoragePath, "started"), [1]);
        Assert.True(await machine.RestoreAsync(options, token));
        await using ServiceProvider native = DotNextNodeServices.Create(localServices!, machine, options, coldStart: false);
        var storage = native.GetRequiredService<IClusterConfigurationStorage<UriEndPoint>>();
        IClusterConfiguration<UriEndPoint> configuration = await storage.LoadConfigurationAsync(token);
        foreach (OperationalNode voter in image.Operations.Nodes.Values)
        {
            configuration = configuration.Add(new UriEndPoint(new Uri(voter.Address)));
        }

        await ((IClusterConfigurationStorage)storage).SaveConfigurationAsync(configuration, image.Operations.MembershipVersion, token);
        IRaftHttpCluster candidate = native.GetRequiredService<IRaftHttpCluster>();
        await candidate.AuditTrail.InitializeAsync(token);
        await candidate.AuditTrail.UpdateTermAsync(image.Term, true, token);
    }

    internal async Task StartAsync(CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        await runtime!.StartNodeAsync(deadline.Token);
    }

    internal async Task ApproveAsync(Guid requestId, CancellationToken token, string? requestDigest = null)
    {
        using IServiceScope scope = leaderServices!.CreateScope();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, authority.ActorId.ToString()),
            new Claim(ClaimTypes.Role, ApplicationRoles.ADMINISTRATOR)], IdentityConstants.ApplicationScheme));
        DefaultHttpContext context = new() { User = user };
        context.Features.Set<IAuthenticateResultFeature>(new AuthenticationResult(AuthenticateResult.Success(
            new AuthenticationTicket(user, IdentityConstants.ApplicationScheme))));
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = context;
        try
        {
            CommandResult accepted = authority.ReadImage().Results[requestId];
            await scope.ServiceProvider.GetRequiredService<VoterContinuationAuthority>().AuthorizeAsync(
                requestId, requestDigest ?? accepted.Digest, VoterContinuationHttpAuthority.PASSWORD, token);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    internal void ChangeBoot()
    {
        lease!.Dispose();
        lease = new(localOptions!, storagePath!);
        var proof = (ManagedRestartProof)admission.Proof!.Proof!;
        admission.Proof = admission.Proof with { Proof = proof with { BootId = lease.BootId, BootChallenge = lease.BootChallenge } };
        var current = Installation.Current!;
        Installation.Publish(current with { BootId = lease.BootId, ProfileRevision = current.ProfileRevision + 1 });
    }

    internal async Task ReplayAcceptedBindAsync(CancellationToken token)
    {
        var image = authority.ReadImage();
        replayImage = image with { OnboardingContracts = image.OnboardingContracts! with
        {
            Genesis = replayImage!.OnboardingContracts!.Genesis
        } };
        var checkpoint = await snapshots!.PublishAsync(replayImage, token);
        admission.Proof = admission.Proof! with
        {
            SnapshotId = checkpoint.Manifest.SnapshotId,
            SnapshotDigest = checkpoint.Digest,
            MinimumCommittedIndex = image.Index,
            Operations = image.Operations
        };

        // Model a locally compacted checkpoint from before this startup attempt.
        Directory.Move(Path.Combine(storagePath!, "wal"), Path.Combine(root, "previous-wal"));
        var options = clusterOptions!;
        Assert.True(await machine!.RestoreAsync(options, token));
        await using ServiceProvider native = DotNextNodeServices.Create(localServices!, machine, options, coldStart: false);
        IRaftHttpCluster candidate = native.GetRequiredService<IRaftHttpCluster>();
        await candidate.AuditTrail.InitializeAsync(token);
        await candidate.AuditTrail.UpdateTermAsync(image.Term, true, token);
        Assert.Equal(image.Index, candidate.AuditTrail.LastCommittedEntryIndex);
    }

    internal void AssertClosed()
    {
        Assert.False(Installation.CommonOpen);
        Assert.False(revisions.Available);
        Assert.False(state!.Accepted.CaughtUp);
        Assert.Null(state.Consensus);
        Assert.Null(handle.Current);
        Assert.Null(runtime!.Raft);
        Assert.False(runtime.CanAcceptCommands);
        Assert.Equal(1, Installation.Transition.CurrentCount);
    }

    public async ValueTask DisposeAsync()
    {
        authority.Dispose();
        gate?.Dispose();
        if (localServices is not null)
        {
            await localServices.DisposeAsync();
        }

        if (leaderServices is not null)
        {
            await leaderServices.DisposeAsync();
        }

        lease?.Dispose();
        Directory.Delete(root, recursive: true);
    }

    private sealed class NoNative(IServiceProvider services, IOptions<ClusterOptions> options, ClusterStateMachine machine,
        ClusterRuntimeState state, NodeReadiness readiness, SenderAuthorityGate gate, TimeProvider time, StartupLog logger,
        ClusterRaftHandle handle, RevisionStateStore revisions)
        : ClusterPeerRuntime(services, options, machine, state, readiness, gate, new(time), time, null!, logger,
            handle, revisions: revisions)
    {
        protected override Task StartNativeAsync(IRaftHttpCluster candidate, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Native Start is forbidden in this test.");
        }
    }

    private sealed class NoNetwork : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => throw new InvalidOperationException("Native network is forbidden.");
    }

    private sealed class Admission : IRecoveryAdmission
    {
        internal RecoveryAdmission? Proof { get; set; }
        public ValueTask<RecoveryAdmission?> ReadAsync(CancellationToken token) => ValueTask.FromResult(Proof);
    }

    private sealed class Projection(Func<RevisionImage> readImage) : IRevisionProjectionStore
    {
        public Task<ProjectionHead?> ReadHeadAsync(CancellationToken token)
        {
            var image = readImage();
            return Task.FromResult<ProjectionHead?>(new(image.ClusterId, image.Generation, image.Index, image.Term, image.Digest));
        }

        public Task VerifyAsync(RevisionImage restored, CancellationToken token)
        {
            Assert.Equal(readImage().Digest, restored.Digest);
            return Task.CompletedTask;
        }

        public Task ApplyAsync(ProjectionWriteSet changes, CancellationToken token) => throw new InvalidOperationException();
        public Task InstallAsync(RevisionImage image, Guid snapshotId, string snapshotDigest, CancellationToken token) => throw new InvalidOperationException();
        public Task<RevisionImage> ExportAsync(RevisionImage image, CancellationToken token) => throw new InvalidOperationException();
        public Task CheckpointAsync(Guid snapshotId, string digest, long index, CancellationToken token) => throw new InvalidOperationException();
    }

    private sealed class StartupLog : ILogger<ClusterPeerRuntime>
    {
        internal List<Guid> Requests { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(state);
            var request = properties.SingleOrDefault(item => item.Key == "RequestId");
            if (request.Value is Guid id)
            {
                Assert.Equal(2, properties.Count);
                Assert.Null(exception);
                Requests.Add(id);
            }
        }
    }
}
