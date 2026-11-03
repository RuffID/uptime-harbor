using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class SetupConsensusTests
{
    [Fact]
    public void MissingTelegramDoesNotBlockWorkConsensusHealthOrForceResign()
    {
        // Arrange
        Guid node = Guid.NewGuid();
        State state = new()
        {
            Consensus = new(Guid.NewGuid(), 1, node, 2, 3, true, 5, new HashSet<Guid> { node }, CancellationToken.None, CancellationToken.None),
            Accepted = new(true, true, true, true, true, 5, 3, Guid.Empty, false)
        };
        NodeReadiness readiness = new(TimeProvider.System);
        foreach (string component in new[] { "Database", "Projection", "Storage", "Clock" })
        {
            readiness.Report(component, true, "Ready", TimeSpan.FromMinutes(1));
        }

        readiness.ConfirmRecovery(readiness.RecoveryVersion, state.Accepted, state.Consensus);

        // Act
        bool beforeToken = readiness.CanMaintainConsensus(state);
        readiness.Report("Telegram", false, "TokenMissing", TimeSpan.FromMinutes(1));
        bool afterToken = readiness.CanMaintainConsensus(state);

        // Assert
        Assert.True(readiness.CanWork(state));
        Assert.True(beforeToken);
        Assert.True(afterToken);
        Assert.False(readiness.CanSend(state));
    }

    private class State : IClusterStateView
    {
        public ConsensusStatus? Consensus { get; set; }
        public AcceptedClusterState Accepted { get; set; } = AcceptedClusterState.Unknown;
    }
}
