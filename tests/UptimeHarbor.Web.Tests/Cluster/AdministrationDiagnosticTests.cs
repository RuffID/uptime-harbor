using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Web.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class AdministrationDiagnosticTests
{
    [Fact]
    public void CancelledConsensusDoesNotPresentLastKnownLeaderAsCurrent()
    {
        // Arrange
        Fixture fixture = new();
        using CancellationTokenSource consensus = new();
        fixture.Consensus = fixture.Consensus! with { ConsensusToken = consensus.Token };

        // Act
        consensus.Cancel();
        ClusterDiagnosticDto result = fixture.Read();

        // Assert
        Assert.Null(result.LeaderNodeId);
        Assert.Equal("Unknown", result.Role);
        Assert.Equal("Unavailable", result.Majority);
        Assert.Equal("Unknown", result.Telegram);
    }

    [Fact]
    public void FollowerWithHealthyDatabaseAndLostPeersIsDistinctFromDatabaseFailure()
    {
        // Arrange
        Fixture fixture = new();
        fixture.Readiness.Report("Database", true, "Ready", TimeSpan.FromHours(1));
        fixture.Connections.Add(fixture.Remote, new("Unavailable", "PeerUnavailable", TimeSpan.FromSeconds(1)));

        // Act
        ClusterDiagnosticDto healthy = fixture.Read();
        fixture.Readiness.Report("Database", false, "DatabaseUnavailable", TimeSpan.FromHours(1));
        ClusterDiagnosticDto failed = fixture.Read();

        // Assert
        Assert.Equal("Follower", healthy.Role);
        Assert.Equal("Unknown", healthy.Majority);
        Assert.Equal("Ready", healthy.LocalDatabase);
        Assert.Equal("Unavailable", healthy.Nodes.Single(node => node.NodeId == fixture.Remote).Connection);
        Assert.Equal("Unknown", healthy.Nodes.Single(node => node.NodeId == fixture.Remote).Database);
        Assert.Equal("Unknown", healthy.Telegram);
        Assert.Equal("Unavailable", failed.LocalDatabase);
        Assert.Equal("Follower", failed.Role);
        Assert.Equal(6, healthy.Lag);
        Assert.Equal(4, healthy.HeldPending);
    }

    [Theory]
    [InlineData(1, "Ready", true, "Available", "Ready")]
    [InlineData(1, "Ready", false, "Unavailable", "SenderNotReady")]
    [InlineData(1, "Unavailable", false, "Unavailable", "TokenMissing")]
    [InlineData(100, "Ready", true, "Unknown", "Unknown")]
    [InlineData(1, "private-credential", true, "Unknown", "Unknown")]
    public void RemoteTelegramIsFreshEvidenceWithClosedReasons_WithoutPromises(int age, string status,
        bool ready, string expected, string reason)
    {
        // Arrange
        Fixture fixture = new();
        PeerHealthSnapshot health = new(fixture.Remote, Guid.NewGuid(), 3, fixture.Remote, 7, 10, 10, ready, 0,
            new Dictionary<string, HealthResult> { ["Telegram"] = new(status, status == "Unavailable" ? "TokenMissing" : "private-credential", TimeSpan.Zero) });
        fixture.Peers.Add(fixture.Remote, new(health, TimeSpan.FromSeconds(age)));

        // Act
        ClusterDiagnosticDto result = fixture.Read();

        // Assert
        Assert.Equal(expected, result.Telegram);
        Assert.Equal(reason, result.TelegramReason);
        Assert.DoesNotContain("private-credential", System.Text.Json.JsonSerializer.Serialize(result));
    }

    private class Fixture : IClusterStateView
    {
        public Guid Local { get; } = Guid.NewGuid();
        public Guid Remote { get; } = Guid.NewGuid();
        public NodeReadiness Readiness { get; } = new(TimeProvider.System);
        public Dictionary<Guid, ObservedPeerHealth> Peers { get; } = [];
        public Dictionary<Guid, HealthResult> Connections { get; } = [];
        public ConsensusStatus? Consensus { get; set; }
        public AcceptedClusterState Accepted { get; } = AcceptedClusterState.Unknown with { AppliedIndex = 4 };
        private readonly RevisionImage image;

        public Fixture()
        {
            image = new(Guid.NewGuid(), 1) { Monitoring = new() { Nodes = ImmutableDictionary<Guid, Guid>.Empty.Add(Local, Guid.NewGuid()).Add(Remote, Guid.NewGuid()) } };
            Consensus = new(image.ClusterId, 1, Local, 3, 7, false, 10, new HashSet<Guid> { Local, Remote }, CancellationToken.None, CancellationToken.None);
        }

        public ClusterDiagnosticDto Read()
        {
            return ClusterBrowserDiagnostics.Project(Local, 30, this, image, Readiness, Remote, Peers, Connections,
                new(5, 4, 0, 4, 10, 6, null, null), DateTimeOffset.UtcNow);
        }
    }
}
