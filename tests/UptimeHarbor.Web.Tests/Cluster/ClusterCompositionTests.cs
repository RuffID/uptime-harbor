using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Web.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class ClusterCompositionTests
{
    [Fact]
    public async Task ActualDotNextGraphResolvesDurableStoreAfterCheckpointWithoutStartingNode()
    {
        // Arrange
        ServiceCollection services = new();
        services.AddLogging();
        services.AddHttpClient();
        NoNetworkHandler handler = new();
        services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => handler));
        using ServiceProvider provider = services.BuildServiceProvider();
        string path = Path.Combine(Path.GetTempPath(), "unresolved-raft-" + Guid.NewGuid().ToString("N"));
        ClusterOptions options = new() { StoragePath = path, PublicUri = "https://peer.example/internal/raft" };
        ClusterStateMachine machine = new(new RestoredCheckpoint(), new NodeReadiness(TimeProvider.System));

        try
        {
            // Act
            await using ServiceProvider node = DotNextNodeServices.Create(provider, machine, options, true);

            // Assert
            Assert.False(Directory.Exists(path));

            // Act
            await machine.RestoreAsync(options, TestContext.Current.CancellationToken);
            IRaftHttpCluster raft = node.GetRequiredService<IRaftHttpCluster>();
            RequestDelegate protocol = DotNextNodeServices.CreateProtocol(node);

            // Assert
            Assert.True(machine.Restored);
            Assert.IsType<WriteAheadLog>(raft.AuditTrail);
            Assert.NotNull(protocol);
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            if (Path.GetDirectoryName(path) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(path).StartsWith("unresolved-raft-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unsafe test cleanup path.");
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [Fact]
    public void ClusterGraphResolvesWithoutHostWalOrHttpClients()
    {
        // Arrange
        ServiceCollection services = new();
        services.AddLogging();
        services.AddScoped<IUserAccessContext, UnknownUser>();
        services.AddScoped<IAdministratorAccessPolicy, AcceptedAdministratorAccessPolicy>();
        services.AddScoped<IAccountCredentialService, UnusedCredentials>();
        services.AddClusterInfrastructure(new ConfigurationBuilder().Build());
        services.AddClusterWeb();
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true, ValidateScopes = true
        });
        using IServiceScope scope = provider.CreateScope();

        // Act
        ClusterPeerRuntime runtime = provider.GetRequiredService<ClusterPeerRuntime>();
        IClusterStateView state = provider.GetRequiredService<IClusterStateView>();
        MembershipCoordinator coordinator = scope.ServiceProvider.GetRequiredService<MembershipCoordinator>();

        // Assert
        Assert.Null(runtime.Raft);
        Assert.Null(state.Consensus);
        Assert.False(state.Accepted.AccountStateConfirmed);
        Assert.False(provider.GetRequiredService<NodeReadiness>().CanSend(state));
        Assert.NotNull(coordinator);
        Assert.IsType<RevisionClusterRecovery>(provider.GetRequiredService<IClusterRecovery>());
        Assert.IsType<UnavailableRecoveryAdmission>(provider.GetRequiredService<IRecoveryAdmission>());
        Assert.IsType<UnavailableSenderConsensus>(provider.GetRequiredService<ISenderConsensus>());
    }

    private class UnknownUser : IUserAccessContext
    {
        public bool IsAuthenticated => false;
        public bool IsAdministrator => false;
        public bool IsEnabled => false;
        public Guid UserId => Guid.Empty;
    }

    private class UnusedCredentials : IAccountCredentialService
    {
        public Task<ServiceResult<ProtectedCredential>> PrepareAsync(Guid userId, string login, string password, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Construction must not prepare credentials.");
        }

        public bool Verify(Guid userId, string login, ProtectedCredential credential, string password)
        {
            throw new InvalidOperationException("Construction must not verify credentials.");
        }
    }

    private class NoNetworkHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("No peer calls are allowed during construction.");
        }
    }

    private class RestoredCheckpoint : IClusterRecovery
    {
        public ClusterSnapshot? Snapshot => null;
        public AcceptedClusterState Accepted => AcceptedClusterState.Unknown;

        public ValueTask<bool> RestoreAsync(Guid clusterId, long generation, Guid nodeId, Guid storageGeneration, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> ConfirmLocalReplayAsync(long committedIndex, long appliedIndex, long membershipVersion, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(false);
        }

        public ValueTask<long> ApplyAsync(CommittedRecord entry, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Construction must not apply entries.");
        }

        public ValueTask ReclaimAsync(long watermark, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Construction must not reclaim storage.");
        }
    }
}
