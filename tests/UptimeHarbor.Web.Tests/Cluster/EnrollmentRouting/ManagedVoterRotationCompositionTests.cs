using System.Data.Common;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using UptimeHarbor.Infrastructure.Persistence;
using UptimeHarbor.Web.Cluster;
using UptimeHarbor.Web.Cluster.EnrollmentRouting;
using UptimeHarbor.Web.Configuration;
using UptimeHarbor.Web.Pages;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class ManagedVoterRotationCompositionTests
{
    [Fact]
    public async Task SingletonReauthorizesProcessAndEnableWithPublishedLeafAndRetiresThroughNativeWriter()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new();
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        Guid node = first.Options.NodeId;
        LocalInstallationProfile profile = first.Installation.Current!;
        Guid oldLeaf = profile.CredentialId!.Value;
        PassClock time = new(first.Time);
        IServiceCollection services = ProductionServices(first.Local);
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(time));
        services.Replace(ServiceDescriptor.Singleton<IOptions<ClusterOptions>>(Options.Create(first.Options)));
        services.Replace(ServiceDescriptor.Singleton(first.Installation));
        services.AddSingleton(first.Lease);
        services.Replace(ServiceDescriptor.Singleton(first.Tls));
        services.Replace(ServiceDescriptor.Singleton(first.Accepted));
        services.Replace(ServiceDescriptor.Singleton(first.Handle));
        services.Replace(ServiceDescriptor.Singleton(first.Runtime));
        services.Replace(ServiceDescriptor.Singleton(first.Readiness));
        services.Replace(ServiceDescriptor.Singleton(first.Admission));
        services.Replace(ServiceDescriptor.Singleton(first.Enable));
        services.Replace(ServiceDescriptor.Singleton(first.Permit));
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory, NoNetworkPeerClients>());
        services.Replace(ServiceDescriptor.Singleton<IEnrollmentSigningUnitOfWork>(first.Store));
        services.Replace(ServiceDescriptor.Singleton<IEnrollmentSigningRepository>(first.Store));
        services.Replace(ServiceDescriptor.Singleton<ILocalSetupUnitOfWork>(first.Store));
        await using ServiceProvider provider = BuildIsolated(services);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var driver = scope.ServiceProvider.GetRequiredService<ManagedBootstrapLifecycle>();
        first.Time.Now = first.Accepted.Current!.Operations.Credentials[oldLeaf].ExpiresAtUtc.AddDays(-5);
        await first.RefreshAsync(token);
        await driver.TickAsync(token);
        await driver.TickAsync(token);
        Assert.Equal(oldLeaf, first.Enable.Current!.CredentialId);
        ManagedWorkerSupervisor supervisor = provider.GetRequiredService<ManagedWorkerSupervisor>();
        await supervisor.PublishReadyAsync(profile, token);
        try
        {
            await time.WaitForPassAsync(token);
            VoterCredentialRotation rotation = Assert.IsType<VoterCredentialRotation>(VoterCredentialPolicy.Latest(first.Accepted.Current!, node));
            Guid newLeaf = rotation.Renewal.Issuance.IssueId;
            Assert.Equal(newLeaf, first.Tls.Capture()!.CredentialId);
            Assert.False(first.Enable.Allows());
            await driver.TickAsync(token);
            Assert.Equal(newLeaf, first.Enable.Current!.CredentialId);

            OnlineProcessLease before = first.Accepted.Current!.OnboardingContracts!.OnlineProcesses![node];
            first.Time.Now = before.LeaseUntilUtc.AddSeconds(-30);
            await first.RefreshAsync(token);
            await driver.TickAsync(token);
            OnlineProcessLease renewed = first.Accepted.Current!.OnboardingContracts!.OnlineProcesses![node];
            Assert.Equal(before.ProcessEpoch, renewed.ProcessEpoch);
            Assert.True(renewed.LeaseUntilUtc > before.LeaseUntilUtc);

            first.Time.Now = rotation.OverlapUntilUtc!.Value;
            await first.RefreshAsync(token);
            await driver.TickAsync(token);
            await driver.TickAsync(token);
            Assert.Equal(newLeaf, first.Enable.Current!.CredentialId);
            await time.NextPassAsync(token);
            RevisionImage retired = first.Accepted.Current!;
            Assert.NotNull(VoterCredentialPolicy.Latest(retired, node)!.RetiredIndex);
            Assert.False(VoterCredentialPolicy.IsUsable(retired, retired.Operations.Credentials[oldLeaf], first.Time.Now));
            Assert.Equal(newLeaf, first.Tls.Capture()!.CredentialId);
        }
        finally
        {
            Assert.True(await supervisor.DrainAsync(profile.RuntimeGeneration, TimeSpan.FromSeconds(5), token));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupervisorRunsTypedRotationThroughRemoteLeaderIssuerAndWitness(bool recover)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using EnrollmentRoutingFixture cluster = NewCluster();
        await cluster.InitializeAsync(token);
        EnrollmentRoutingNode subject = cluster.Ingress;
        PassClock time = new(cluster.First.Time);
        SetRenewalWindow(cluster, subject);
        await using ServiceProvider issuer = Compose(cluster, cluster.Issuer, cluster.First.Time);
        await using ServiceProvider leader = Compose(cluster, cluster.Leader, cluster.First.Time);
        await using ServiceProvider caller = Compose(cluster, subject, time);
        cluster.ControllerServices.Add(cluster.Issuer.Options.NodeId, issuer);
        cluster.ControllerServices.Add(cluster.Leader.Options.NodeId, leader);
        ManagedWorkerSupervisor supervisor = caller.GetRequiredService<ManagedWorkerSupervisor>();
        LocalInstallationProfile profile = subject.Installation.Current!;
        Guid oldLeaf = subject.Options.CredentialId;
        int issued = cluster.Issuer.Store.Issues.Count;

        if (recover)
        {
            cluster.LoseResponse = EnrollmentPeerProtocol.ROTATION_AUTHORIZE;
        }

        await supervisor.PublishReadyAsync(profile, token);
        try
        {
            await time.WaitForPassAsync(token);
            if (recover)
            {
                CommandResult authorization = cluster.State.Current!.Results[Latest().AuthorizationOperationId];
                Assert.Equal(OperationOutcome.Committed, authorization.Outcome);
                Assert.Equal(oldLeaf, subject.Tls.Capture()!.CredentialId);

                cluster.EditBody = (path, bytes) =>
                {
                    if (path != EnrollmentPeerProtocol.ROTATION_BIND)
                    {
                        return bytes;
                    }

                    var call = StrictOnboardingJson.Decode<VoterCredentialBindCall>(bytes);
                    return StrictOnboardingJson.Encode(call with { BindProof = call.Signing.ProofRequest.Proof });
                };
                await time.NextPassAsync(token);
                Assert.Null(Latest().BindOperationId);
                Assert.Equal(oldLeaf, subject.Tls.Capture()!.CredentialId);

                cluster.EditBody = null;
                cluster.EditResponse = (path, bytes) => path == EnrollmentPeerProtocol.ROTATION_BIND
                    ? StrictOnboardingJson.Encode(StrictOnboardingJson.Decode<CommandResult>(bytes) with { Index = authorization.Index })
                    : bytes;
                await time.NextPassAsync(token);
                Assert.Equal(OperationOutcome.Committed, cluster.State.Current!.Results[Latest().BindOperationId!.Value].Outcome);
                Assert.Equal(oldLeaf, subject.Tls.Capture()!.CredentialId);
                var sentBind = cluster.Sent.Where(call => call.Path == EnrollmentPeerProtocol.ROTATION_BIND)
                    .Select(call => StrictOnboardingJson.Decode<VoterCredentialBindCall>(call.Body).Command).ToArray();
                Assert.Equal(2, sentBind.Length);
                Assert.Equal(sentBind[0], sentBind[1]);
                Assert.Equal(authorization, cluster.State.Current!.Results[authorization.OperationId]);
                cluster.EditResponse = null;
                await time.NextPassAsync(token);
            }

            var rotation = Latest();
            var published = Assert.IsType<DynamicPeerTls.Snapshot>(subject.Tls.Capture());
            Assert.Equal(rotation.Renewal.Issuance.IssueId, published.CredentialId);
            Assert.Equal(rotation.CredentialRevision, published.CredentialRevision);
            Assert.Equal(Convert.FromBase64String(cluster.State.Current!.Operations.Credentials[rotation.Renewal.Issuance.IssueId]
                .PublicCertificate), published.Certificate.RawData);
            Assert.Equal(issued + 1, cluster.Issuer.Store.Issues.Count);
            Assert.Single(cluster.Sent, call => call.Path == EnrollmentPeerProtocol.ROTATION_AUTHORIZE);
            Assert.Single(cluster.Sent, call => call.Path == EnrollmentPeerProtocol.RENEW && call.Target == cluster.Issuer.Options.NodeId);
            Assert.Equal(recover ? 2 : 1, cluster.Sent.Count(call => call.Path == EnrollmentPeerProtocol.ROTATION_BIND));
            Assert.DoesNotContain(cluster.Sent, call => call.Path == EnrollmentPeerProtocol.ROTATION_RETIRE);

            cluster.First.Time.Now = rotation.OverlapUntilUtc!.Value;
            if (recover)
            {
                cluster.LoseResponse = EnrollmentPeerProtocol.ROTATION_RETIRE;
            }

            await time.NextPassAsync(token);
            var witness = Assert.Single(cluster.Sent, call => call.Path == EnrollmentPeerProtocol.RETIREMENT_HANDOFF);
            Assert.NotEqual(subject.Options.NodeId, witness.Target);
            var retire = Assert.Single(cluster.Sent, call => call.Path == EnrollmentPeerProtocol.ROTATION_RETIRE);
            Assert.Equal(cluster.Leader.Options.NodeId, retire.Target);
            var intent = StrictOnboardingJson.Decode<VoterCredentialRetirementCall>(retire.Body);
            Assert.Equal(witness.Target, intent.Evidence!.NodeId);
            Assert.Equal(published.Fingerprint, intent.Intent.Handoff.Fingerprint);
            Assert.Equal(intent.Intent.Command.OperationId, Latest().RetireOperationId);
            Assert.False(VoterCredentialPolicy.IsUsable(cluster.State.Current!, cluster.State.Current!.Operations.Credentials[oldLeaf], time.GetUtcNow()));

            await time.NextPassAsync(token);
            Assert.Single(cluster.Sent, call => call.Path == EnrollmentPeerProtocol.ROTATION_RETIRE);
            Assert.Same(published, subject.Tls.Capture());
        }
        finally
        {
            Assert.True(await supervisor.DrainAsync(profile.RuntimeGeneration, TimeSpan.FromSeconds(5), token));
        }

        VoterCredentialRotation Latest()
        {
            return Assert.Single(cluster.State.Current!.OnboardingContracts!.VoterCredentialRotations!.Values);
        }
    }

    [Fact]
    public async Task WorkerRejectsStaleGenerationAndMissingMajority()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using EnrollmentRoutingFixture cluster = NewCluster();
        await cluster.InitializeAsync(token);
        EnrollmentRoutingNode subject = cluster.Ingress;
        PassClock time = new(cluster.First.Time);
        await using ServiceProvider caller = Compose(cluster, subject, time);
        ManagedWorkerSupervisor supervisor = caller.GetRequiredService<ManagedWorkerSupervisor>();
        ManagedWorkerCatalog catalog = caller.GetRequiredService<ManagedWorkerCatalog>();
        LocalInstallationProfile profile = subject.Installation.Current!;
        Assert.Empty(catalog.Resolve(true, true, true).OfType<VoterCredentialRotationWorker>());
        Assert.Empty(catalog.Resolve(true, false, false).OfType<VoterCredentialRotationWorker>());
        subject.Options.TlsBoundary = "NginxIsolatedHttp";
        Assert.Empty(catalog.Resolve(true, false, true).OfType<VoterCredentialRotationWorker>());
        subject.Options.TlsBoundary = "Direct";
        Assert.Single(catalog.Resolve(true, false, true).OfType<VoterCredentialRotationWorker>());
        await supervisor.PublishReadyAsync(profile, token);
        try
        {
            await time.WaitForPassAsync(token);
            Assert.Empty(cluster.Sent);

            SetRenewalWindow(cluster, subject);
            cluster.Leader.Native.OnBarrier = () => throw new InvalidOperationException("TestMajorityUnavailable");
            await time.NextPassAsync(token);
            Assert.Equal(EnrollmentPeerProtocol.ROTATION_NONCE, Assert.Single(cluster.Sent).Path);
            Assert.Empty(cluster.State.Current!.OnboardingContracts!.VoterCredentialRotations ?? []);

            cluster.Leader.Native.OnBarrier = null;
            subject.Installation.Publish(profile with
            {
                RuntimeGeneration = profile.RuntimeGeneration + 1, ProfileRevision = profile.ProfileRevision + 1
            });
            await time.NextPassAsync(token);
            Assert.Single(cluster.Sent);
            Assert.Null(subject.Tls.Capture());
        }
        finally
        {
            Assert.True(await supervisor.DrainAsync(profile.RuntimeGeneration, TimeSpan.FromSeconds(5), token));
        }
    }

    private static EnrollmentRoutingFixture NewCluster()
    {
        return new()
        {
            RenewalEnabled = true, RotationAdmissionEnabled = true, UseManagedPools = true, UsePublishedPeerLeaves = true
        };
    }

    private static void SetRenewalWindow(EnrollmentRoutingFixture cluster, EnrollmentRoutingNode subject)
    {
        RevisionImage image = cluster.State.Current!;
        DateTimeOffset now = image.Operations.Credentials[subject.Options.CredentialId].ExpiresAtUtc.AddDays(-5);
        cluster.First.Time.Now = now;
        OnlineProcessLease process = new(subject.Options.NodeId, Guid.NewGuid(), subject.Lease.BootId, subject.Lease.BootChallenge,
            1, 0, now.AddMinutes(10), image.Index);
        var contracts = image.OnboardingContracts!;
        cluster.State.Publish(image with { OnboardingContracts = contracts with
        {
            OnlineProcesses = contracts.OnlineProcesses!.SetItem(process.NodeId, process)
        } });
        cluster.Refresh();
    }

    private static ServiceProvider Compose(EnrollmentRoutingFixture cluster, EnrollmentRoutingNode node, TimeProvider time)
    {
        IServiceCollection services = ProductionServices(node.Local);
        services.Replace(ServiceDescriptor.Singleton(time));
        services.Replace(ServiceDescriptor.Singleton<IOptions<ClusterOptions>>(Options.Create(node.Options)));
        services.Replace(ServiceDescriptor.Singleton(node.Installation));
        services.AddSingleton(node.Lease);
        services.Replace(ServiceDescriptor.Singleton(node.Tls));
        services.Replace(ServiceDescriptor.Singleton(cluster.State));
        services.Replace(ServiceDescriptor.Singleton(node.Handle));
        services.Replace(ServiceDescriptor.Singleton(node.View));
        services.Replace(ServiceDescriptor.Singleton(node.Readiness));
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(node.PeerClients));
        services.Replace(ServiceDescriptor.Singleton<IConsensusWriter>(node.Writer));
        services.Replace(ServiceDescriptor.Singleton<IEnrollmentSigningUnitOfWork>(node.Store));
        services.Replace(ServiceDescriptor.Singleton<IEnrollmentSigningRepository>(node.Store));
        services.Replace(ServiceDescriptor.Singleton<IManagedRuntimeAdmission, ReadyProfileAdmission>());
        return BuildIsolated(services);
    }

    private static IServiceCollection ProductionServices(InstallationOptions local)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = []
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Installation:InstallationIntent"] = "Setup",
            ["Installation:StateDirectory"] = local.StateDirectory,
            ["Installation:SecretDirectory"] = local.SecretDirectory,
            ["Installation:KeyDirectory"] = local.KeyDirectory,
            ["Installation:BrowserOrigin"] = local.BrowserOrigin,
            ["Installation:PeerEndpoint"] = local.PeerEndpoint,
            ["ConnectionStrings:SqlServer"] = "Isolated rotation forbids SQL"
        });
        IServiceCollection services = builder.Services;
        services.AddApplication().AddApplicationConfiguration(builder.Configuration)
            .AddInfrastructure(builder.Configuration).AddWebPresentation().AddClusterWeb(builder.Configuration);
        services.RemoveAll<IDbContextOptionsConfiguration<UptimeHarborDbContext>>();
        services.ConfigureDbContext<UptimeHarborDbContext>(options => options.AddInterceptors(new ForbiddenSql()));
        return services;
    }

    private static ServiceProvider BuildIsolated(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IRevisionTransport, ForbiddenBatch>());
        services.AddSingleton<ManagedWorkerCatalog>();
        services.Replace(ServiceDescriptor.Singleton<IManagedWorkerCatalog, RotationOnlyCatalog>());
        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using IServiceScope scope = provider.CreateScope();
        DbContextOptions<UptimeHarborDbContext> options = scope.ServiceProvider.GetRequiredService<DbContextOptions<UptimeHarborDbContext>>();
        Assert.DoesNotContain(options.Extensions, extension => extension.Info.IsDatabaseProvider);

        return provider;
    }

    // Штатный каталог выбирает worker ротации. Остальные workers в тесте не стартуют.
    private sealed class RotationOnlyCatalog(ManagedWorkerCatalog catalog) : IManagedWorkerCatalog
    {
        public IReadOnlyList<RestartableWorker> Resolve(bool cluster, bool quarantine, bool managedSetup)
        {
            return catalog.Resolve(cluster, quarantine, managedSetup).OfType<VoterCredentialRotationWorker>().ToArray();
        }
    }

    private sealed class ReadyProfileAdmission(InstallationState installation) : IManagedRuntimeAdmission
    {
        public Task<bool> CanStartAsync(LocalInstallationProfile profile, CancellationToken token)
        {
            return Task.FromResult(false);
        }

        public Task<bool> CanWorkAsync(LocalInstallationProfile profile, CancellationToken token)
        {
            return Task.FromResult(installation.CommonOpen && installation.Current == profile);
        }
    }

    private sealed class ForbiddenBatch : IRevisionTransport
    {
        public Task<IReadOnlyList<CommandResult>> SendAsync(IReadOnlyList<RevisionCommand> commands, CancellationToken token)
        {
            throw new InvalidOperationException("RotationMustNotUseBatch");
        }
    }

    private sealed class ForbiddenSql : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            throw new InvalidOperationException("RotationCompositionMustNotOpenDatabase");
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("RotationCompositionMustNotOpenDatabase");
        }
    }

    private sealed class PassClock(ManagedBootstrapFixture.Clock clock) : TimeProvider
    {
        private readonly Channel<PassTimer> passes = Channel.CreateUnbounded<PassTimer>();
        private PassTimer? scheduled;

        public override DateTimeOffset GetUtcNow()
        {
            return clock.Now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != TimeSpan.FromMinutes(1))
            {
                return base.CreateTimer(callback, state, dueTime, period);
            }

            PassTimer timer = new(callback, state);
            Assert.True(passes.Writer.TryWrite(timer));
            return timer;
        }

        internal async Task WaitForPassAsync(CancellationToken token)
        {
            scheduled = await passes.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(45), token);
        }

        internal Task NextPassAsync(CancellationToken token)
        {
            scheduled!.Tick();
            return WaitForPassAsync(token);
        }

        private sealed class PassTimer(TimerCallback callback, object? state) : ITimer
        {
            public void Tick()
            {
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}
