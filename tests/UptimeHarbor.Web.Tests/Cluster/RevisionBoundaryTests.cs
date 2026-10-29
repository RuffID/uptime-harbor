using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Web.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class RevisionBoundaryTests
{
    [Fact]
    public async Task ObservationStorageMustMatchAuthenticatedPeerBeforeDispatch()
    {
        // Arrange
        Guid clusterId = Guid.NewGuid();
        Guid nodeId = Guid.NewGuid();
        Guid storage = Guid.NewGuid();
        Guid targetId = Guid.NewGuid();
        MonitoringObservation observation = new(Guid.NewGuid(), targetId, nodeId, storage, Guid.NewGuid(), 1, 1,
            DateTimeOffset.UnixEpoch, 1, true, null, "app", null, true);
        RevisionCommand command = MonitoringCommands.Record(new(clusterId, 1), nodeId, storage, [observation]);
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new[] { command }));
        context.Items[typeof(PeerEnvelope)] = new PeerEnvelope(clusterId, 1, nodeId, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch, "1.0/1/1/1/1/6.10.0", Guid.NewGuid());
        Consensus consensus = new();
        RevisionStateStore state = new();
        state.Publish(new(clusterId, 1));
        ClusterRevisionController controller = new(new(consensus, state), Options.Create(new ClusterOptions()))
        {
            ControllerContext = new() { HttpContext = context }
        };

        // Act
        IActionResult result = await controller.SubmitAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Equal(0, consensus.Calls);
    }

    [Theory]
    [InlineData(false, 403)]
    [InlineData(true, 400)]
    public async Task MissingPeerOrInvalidBatchTailCannotAcceptEvenFirstCommand(bool verifiedPeer, int expectedStatus)
    {
        // Arrange
        Guid clusterId = Guid.NewGuid();
        Guid nodeId = Guid.NewGuid();
        RevisionCommand command = new()
        {
            ClusterId = clusterId, ClusterGeneration = 1, OperationId = Guid.NewGuid(), OriginNodeId = nodeId,
            ActorUserId = Guid.NewGuid(), ActorRevision = 1, EntityId = Guid.NewGuid(), EntityKind = DeletedEntityKind.Server,
            BaseRevision = 0, Action = "Upsert", CreatedAtUtc = DateTimeOffset.UnixEpoch,
            Value = new ConfigurationValue { Name = "server", Endpoint = "https://test.invalid/health", Generation = Guid.NewGuid() }
        };
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new[] { command, command with { Schema = 99 } }));
        if (verifiedPeer)
        {
            context.Items[typeof(PeerEnvelope)] = new PeerEnvelope(clusterId, 1, nodeId, Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch, "1.0/1/1/1/1/6.10.0", Guid.NewGuid());
        }

        Consensus consensus = new();
        RevisionStateStore state = new();
        state.Publish(new RevisionImage(clusterId, 1));
        ClusterRevisionController controller = new(new RevisionDispatcher(consensus, state), Options.Create(new ClusterOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        // Act
        IActionResult result = await controller.SubmitAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedStatus, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Equal(0, consensus.Calls);
    }

    private class Consensus : IConsensusWriter
    {
        public int Calls { get; private set; }

        public ValueTask<long> ReplicateAsync(ReadOnlyMemory<byte> payload, int commandId, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("Invalid batch must not reach consensus.");
        }

        public ValueTask<bool> ResignAsync(CancellationToken token)
        {
            throw new NotSupportedException();
        }
    }
}
