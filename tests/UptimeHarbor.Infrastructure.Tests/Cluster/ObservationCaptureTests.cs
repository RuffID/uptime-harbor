using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Persistence.UnitOfWork;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class ObservationCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionCaptureRetriesRestartAndSequenceShareTransaction(bool afterCommit)
    {
        // Arrange
        var (image, observation) = Scenario();
        RevisionStateStore state = new();
        state.Publish(image);
        ObservationMemory storage = new() { FailSave = !afterCommit, LoseCommitResponse = afterCommit };
        ObservationCaptureService service = new(new ObservationCaptureUnitOfWork(storage, storage), new(), state);
        CancellationToken token = TestContext.Current.CancellationToken;

        // Act
        Exception? failure = await Record.ExceptionAsync(() => service.CaptureAsync(observation, token));
        ObservationMemory restarted = new(storage.Saved);
        ObservationCaptureGate gate = new();
        ObservationCaptureService retry = new(new ObservationCaptureUnitOfWork(restarted, restarted), gate, state);
        CapturedObservation? first = await retry.CaptureAsync(observation, token);
        CapturedObservation? duplicate = await retry.CaptureAsync(observation, token);
        CapturedObservation? next = await retry.CaptureAsync(observation with { Id = Guid.NewGuid(), CheckedAtUtc = observation.CheckedAtUtc.AddMinutes(1) }, token);

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(afterCommit ? 1 : 0, storage.Saved.Count);
        Assert.Equal(first, duplicate);
        Assert.Equal(1, first!.Sequence);
        Assert.Equal(2, next!.Sequence);
        Assert.Equal(2, restarted.Saved.Count);

        // Act
        Exception? reused = await Record.ExceptionAsync(() => retry.CaptureAsync(observation with { Succeeded = false, FailureKind = "Timeout", EndpointName = null }, token));
        CapturedObservation? stale = await retry.CaptureAsync(observation with { Id = Guid.NewGuid(), CheckGeneration = Guid.NewGuid() }, token);

        // Assert
        Assert.IsType<InvalidDataException>(reused);
        Assert.Null(stale);
        Assert.Equal(2, restarted.Saved.Count);
    }

    internal static (RevisionImage Image, MonitoringObservation Observation) Scenario()
    {
        RevisionFixture fixture = new();
        Guid storage = Guid.NewGuid();
        RevisionImage admitted = fixture.Baseline with { Monitoring = new MonitoringImage
        {
            Nodes = ImmutableDictionary<Guid, Guid>.Empty.Add(fixture.Node, storage)
        } };
        RevisionCommand command = fixture.Server();
        command = command with { Value = command.Value! with { ObserverNodes = [fixture.Node] } };
        RevisionImage image = new RevisionReducer().Apply(admitted, command, 3, 1);
        AcceptedValue target = image.Values[command.EntityId];
        MonitoringObservation observation = new(Guid.NewGuid(), target.Id, fixture.Node, storage, target.Value.Generation,
            target.Revision, 1, DateTimeOffset.UnixEpoch.AddMinutes(1), 1, true, null, "app", null, true);
        return (image, observation);
    }
}
