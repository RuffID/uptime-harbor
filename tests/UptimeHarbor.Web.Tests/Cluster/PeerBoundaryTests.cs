using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Web.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class PeerBoundaryTests
{
    [Theory]
    [InlineData("/internal/cluster/v1/health", 443, 404)]
    [InlineData("/api/admin/notifications", 8443, 404)]
    [InlineData("/api/admin/notifications", 443, 503)]
    [InlineData("/internal/cluster/v1/health", 8443, 401)]
    public async Task PeerListenerNeverFallsThroughToCookieEndpoints(string path, int port, int expected)
    {
        // Arrange
        ClusterOptions options = Options();
        NodeReadiness readiness = ReadyClock();
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        using SenderAuthorityGate gate = new(new ClusterRuntimeState(new UnavailableClusterRecovery()), readiness,
            new UnavailableSenderConsensus(), TimeProvider.System);
        using ClusterPeerRuntime runtime = Runtime(services, options, readiness, gate);
        bool nextCalled = false;
        PeerBoundaryMiddleware middleware = new(context =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, Microsoft.Extensions.Options.Options.Create(options));
        DefaultHttpContext context = Context(path, port);
        context.Request.Headers.Cookie = "auth=secret-cookie";

        // Act
        await middleware.InvokeAsync(context, new UnavailablePeerTrustSource(), readiness, TimeProvider.System, runtime);

        // Assert
        Assert.Equal(expected, context.Response.StatusCode);
        Assert.False(nextCalled);
        Assert.Equal(0, context.Response.Headers.SetCookie.Count);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task OversizedBodyIsRejectedBeforeTrustOrCertificateLookup()
    {
        // Arrange
        ClusterOptions options = Options();
        NodeReadiness readiness = ReadyClock();
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        using SenderAuthorityGate gate = new(new ClusterRuntimeState(new UnavailableClusterRecovery()), readiness,
            new UnavailableSenderConsensus(), TimeProvider.System);
        using ClusterPeerRuntime runtime = Runtime(services, options, readiness, gate);
        PeerBoundaryMiddleware middleware = new(_ => throw new InvalidOperationException(),
            Microsoft.Extensions.Options.Options.Create(options));
        DefaultHttpContext context = Context("/internal/cluster/v1/health", 8443);
        context.Request.ContentLength = 65537;

        // Act
        await middleware.InvokeAsync(context, new UnavailablePeerTrustSource(), readiness, TimeProvider.System, runtime);

        // Assert
        Assert.Equal(413, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task UnconfirmedRecoveryDoesNotCreateWalOrResolveDotNext()
    {
        // Arrange
        ClusterOptions options = Options();
        options.StoragePath = Path.Combine(Path.GetTempPath(), "unprovisioned-" + Guid.NewGuid().ToString("N"));
        NodeReadiness readiness = ReadyClock();
        readiness.Report("Database", true, "Ready", TimeSpan.FromMinutes(1));
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        using SenderAuthorityGate gate = new(new ClusterRuntimeState(new UnavailableClusterRecovery()), readiness,
            new UnavailableSenderConsensus(), TimeProvider.System);
        using ClusterPeerRuntime runtime = Runtime(services, options, readiness, gate);

        // Act
        await runtime.StartNodeAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(runtime.Raft);
        Assert.False(Directory.Exists(options.StoragePath));
    }

    private static ClusterOptions Options()
    {
        return new() { Mode = "Restart", PublicUri = "https://peer.example/internal/raft", PeerListenPort = 8443 };
    }

    private static NodeReadiness ReadyClock()
    {
        NodeReadiness readiness = new(TimeProvider.System);
        readiness.Report("Clock", true, "Ready", TimeSpan.FromMinutes(1));
        return readiness;
    }

    private static DefaultHttpContext Context(string path, int port)
    {
        DefaultHttpContext context = new();
        context.Request.Path = path;
        context.Request.Method = "GET";
        context.Request.ContentLength = 0;
        context.Request.Host = new("peer.example");
        context.Connection.LocalPort = port;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static ClusterPeerRuntime Runtime(IServiceProvider services, ClusterOptions options,
        NodeReadiness readiness, SenderAuthorityGate gate)
    {
        UnavailableClusterRecovery recovery = new();
        return new(services, Microsoft.Extensions.Options.Options.Create(options), new ClusterStateMachine(recovery, readiness),
            new ClusterRuntimeState(recovery), readiness, gate, new LeaderFailureWindow(TimeProvider.System),
            TimeProvider.System, new UnavailablePeerTrustSource(), NullLogger<ClusterPeerRuntime>.Instance, new ClusterRaftHandle());
    }
}
