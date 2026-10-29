using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class MonitoringAvailabilityTests
{
    [Fact]
    public void AssignedObserversKeepIndependentCountersAndMetadata()
    {
        // Arrange
        MonitoringScenario scenario = new();
        scenario.Observe(scenario.A, 1, true, "application-A");
        scenario.Observe(scenario.B, 1, true, "application-B");
        scenario.Evaluate(2);

        // Act
        CommandResult unassigned = scenario.Observe(scenario.C, 2, false);
        scenario.Observe(scenario.A, 60, false);
        scenario.Observe(scenario.B, 60, false);
        scenario.Observe(scenario.A, 120, false);
        scenario.Observe(scenario.B, 120, true, "changed-B");
        scenario.Evaluate(121);

        // Assert
        MonitoringEvaluation available = scenario.State.Monitoring.Live[scenario.TargetId];
        Assert.Equal("ObserverNotAssigned", unassigned.Reason);
        Assert.Equal(AvailabilityState.Available, available.Availability);
        Assert.Equal(CoverageState.Complete, available.Coverage);
        Assert.Equal("application-A", available.Metadata[scenario.A].EndpointName);
        Assert.Equal("changed-B", available.Metadata[scenario.B].EndpointName);
        Assert.Empty(scenario.State.Monitoring.Incidents);

        // Act
        scenario.Observe(scenario.A, 180, false);
        scenario.Observe(scenario.B, 180, true);
        scenario.Evaluate(181);

        // Assert
        MonitoringEvaluation partial = scenario.State.Monitoring.Live[scenario.TargetId];
        Assert.Equal(AvailabilityState.Partial, partial.Availability);
        Assert.Equal([scenario.A], partial.ProblemNodes);
        Assert.Single(scenario.State.Monitoring.Incidents);
        Assert.Single(scenario.State.Monitoring.Events);
        Assert.Single(scenario.State.Monitoring.Intents);
    }

    [Fact]
    public void CoverageLossDoesNotCloseAnOutageOrCreateFullTargetFailure()
    {
        // Arrange
        MonitoringScenario scenario = new();
        foreach (int second in new[] { 1, 60, 120 })
        {
            scenario.Observe(scenario.A, second, false);
            scenario.Observe(scenario.B, second, true);
        }

        scenario.Evaluate(121);
        Guid outage = scenario.State.Monitoring.Incidents.Values.Single().Id;

        // Act
        scenario.Observe(scenario.B, 240, true);
        scenario.Evaluate(280);
        scenario.Evaluate(450);

        // Assert
        Assert.Equal(AvailabilityState.Unknown, scenario.State.Monitoring.Live[scenario.TargetId].Availability);
        Assert.Equal(CoverageState.Lost, scenario.State.Monitoring.Live[scenario.TargetId].Coverage);
        Assert.Null(scenario.State.Monitoring.Incidents[outage].CompletedAtUtc);
        MonitoringIncident coverage = scenario.State.Monitoring.Incidents.Values.Single(item => item.Kind == IncidentKind.Coverage);
        Assert.Equal(2, coverage.EventSequence);
        Assert.DoesNotContain(scenario.State.Monitoring.Intents.Values, item => item.Kind == DeliveryKind.Recovery);

        // Act
        scenario.Observe(scenario.A, 451, true);
        scenario.Observe(scenario.B, 451, true);
        scenario.Evaluate(452);

        // Assert
        Assert.Equal(IncidentCompletion.Recovered, scenario.State.Monitoring.Incidents[outage].Completion);
        Assert.Equal(IncidentCompletion.Recovered, scenario.State.Monitoring.Incidents[coverage.Id].Completion);
        Assert.DoesNotContain(scenario.State.Monitoring.Intents.Values, item => item.Kind == DeliveryKind.Recovery);
    }

    [Fact]
    public void FailureBelowThresholdExpiresPreviousSuccessAndUncertainClockIsNotFresh()
    {
        // Arrange
        MonitoringScenario scenario = new();
        scenario.Observe(scenario.A, 1, true);
        scenario.Observe(scenario.B, 1, true);
        scenario.Observe(scenario.A, 120, false);
        MonitoringObservation uncertain = scenario.Observation(scenario.B, 160, true) with { ClockConfirmed = false };
        scenario.Record(uncertain);

        // Act
        scenario.Evaluate(162);

        // Assert
        Assert.Equal(AvailabilityState.Unknown, scenario.State.Monitoring.Live[scenario.TargetId].Availability);
        Assert.Equal(CoverageState.Lost, scenario.State.Monitoring.Live[scenario.TargetId].Coverage);
        Assert.All(scenario.State.Monitoring.Incidents.Values, item => Assert.Equal(IncidentKind.Coverage, item.Kind));
        Assert.True(scenario.State.Monitoring.Acceptances[uncertain.Id].HistoricalOnly);
    }

    [Fact]
    public void StaleGenerationDisabledAndDeletedResultsRemainHistorical()
    {
        // Arrange
        MonitoringScenario scenario = new();
        MonitoringObservation stale = scenario.Observation(scenario.A, 2, false);
        AcceptedValue original = scenario.State.Values[scenario.TargetId];
        RevisionCommand change = scenario.Command(original.Id, original.Kind, original.Revision,
            original.Value with { Generation = Guid.NewGuid(), Endpoint = "https://new.invalid/health" });
        scenario.Apply(change);

        // Act
        scenario.Record(stale);
        AcceptedValue changed = scenario.State.Values[scenario.TargetId];
        MonitoringObservation disabled = scenario.Observation(scenario.B, 3, false);
        scenario.Apply(scenario.Command(changed.Id, changed.Kind, changed.Revision,
            changed.Value with { Enabled = false, Generation = Guid.NewGuid() }));
        scenario.Record(disabled);
        AcceptedValue current = scenario.State.Values[scenario.TargetId];
        scenario.Apply(scenario.Command(current.Id, current.Kind, current.Revision, null) with { Action = "Delete" });
        scenario.Record(stale with { Id = Guid.NewGuid(), Sequence = 2 });
        CommandResult fabricated = scenario.Record(stale with { Id = Guid.NewGuid(), CheckGeneration = Guid.NewGuid() });
        CommandResult neverAssigned = scenario.Record(stale with
        {
            Id = Guid.NewGuid(), ObserverNodeId = scenario.C, StorageGeneration = scenario.State.Monitoring.Nodes[scenario.C]
        });

        // Assert
        Assert.Empty(scenario.State.Monitoring.Incidents);
        Assert.Equal(3, scenario.State.Monitoring.Observations.Count);
        Assert.All(scenario.State.Monitoring.Acceptances.Values, value => Assert.True(value.HistoricalOnly));
        Assert.True(scenario.State.Values[scenario.TargetId].Deleted);
        Assert.Equal("ObservationConfigurationUnknown", fabricated.Reason);
        Assert.Equal("ObservationConfigurationUnknown", neverAssigned.Reason);
    }

    [Fact]
    public void GapsIdReuseAndReplayCannotMoveStreamHeadOrDuplicateEvents()
    {
        // Arrange
        MonitoringScenario scenario = new();
        MonitoringObservation first = scenario.Observation(scenario.A, 1, false);
        MonitoringObservation gap = first with { Id = Guid.NewGuid(), Sequence = 3 };

        // Act
        CommandResult missing = scenario.Record(gap);
        scenario.Record(first);
        scenario.Record(first);
        Exception? reused = Record.Exception(() => scenario.Record(first with { Succeeded = true, FailureKind = null }));

        // Assert
        Assert.Equal("ObservationGap", missing.Reason);
        Assert.Single(scenario.State.Monitoring.Observations);
        Assert.Equal(1, scenario.State.Monitoring.Streams.Values.Single().Sequence);
        Assert.IsType<InvalidDataException>(reused);
    }
}
