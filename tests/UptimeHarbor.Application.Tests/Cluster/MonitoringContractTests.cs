using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class MonitoringContractTests
{
    [Fact]
    public void RevokedSummaryPreservesOtherSourcesForANewImmutablePlan()
    {
        // Arrange
        MonitoringScenario scenario = new();
        RevisionCommand create = scenario.Server();
        create = create with { CreatedAtUtc = scenario.Start, Value = create.Value! with { Name = "second", ObserverNodes = [scenario.A, scenario.B] } };
        scenario.Apply(create);
        Guid otherId = create.EntityId;
        scenario.Apply(scenario.Command(Guid.NewGuid(), DeletedEntityKind.Binding, 0, new ConfigurationValue
        {
            ParentId = otherId, ChatId = scenario.ChatId, Generation = Guid.NewGuid(), Enabled = true
        }));
        CompleteTarget(scenario, scenario.TargetId);
        CompleteTarget(scenario, otherId);
        scenario.Accept(scenario.Backlog(), 2);
        MonitoringSummary original = Assert.Single(scenario.State.Monitoring.Summaries.Values);
        Assert.Equal(2, original.SourceIntentIds.Length);
        AcceptedValue binding = scenario.State.Values.Values.Single(item => item.Kind == DeletedEntityKind.Binding && item.Value.ParentId == scenario.TargetId);

        // Act
        CommandResult removed = scenario.Accept(scenario.Command(binding.Id, binding.Kind, binding.Revision, null) with { Action = "Delete" }, 2);
        MonitoringIntent remaining = scenario.State.Monitoring.Intents.Values.Single(item => item.IncidentId.HasValue
            && scenario.State.Monitoring.Incidents[item.IncidentId.Value].TargetId == otherId);

        // Assert
        Assert.Equal(OperationOutcome.Committed, removed.Outcome);
        Assert.Equal(DeliveryState.Cancelled, scenario.State.Monitoring.Intents[original.IntentId].State);
        Assert.Equal(DeliveryState.Planned, remaining.State);
        Assert.Null(remaining.SummaryId);

        // Act
        scenario.Accept(scenario.Backlog(), 2);

        // Assert
        Assert.Equal(2, scenario.State.Monitoring.Summaries.Count);
        Assert.Equal(original, scenario.State.Monitoring.Summaries[original.Id]);
        MonitoringSummary next = scenario.State.Monitoring.Summaries.Values.Single(item => item.Id != original.Id);
        Assert.Equal([remaining.Id], next.SourceIntentIds);
        Assert.Equal(DeliveryState.SuppressedBySummary, scenario.State.Monitoring.Intents[remaining.Id].State);
        Assert.Equal(4, scenario.State.Monitoring.Events.Count);
    }

    [Fact]
    public void ContainerMetadataStaysPerObserverAndDoesNotCreateFalseOutage()
    {
        // Arrange
        MonitoringScenario scenario = new();
        Guid id = Guid.NewGuid();
        scenario.Apply(scenario.Command(id, DeletedEntityKind.Container, 0, new ConfigurationValue
        {
            Endpoint = "https://test.invalid/container", Generation = Guid.NewGuid(), ObserverNodes = [scenario.A, scenario.B]
        }) with { CreatedAtUtc = scenario.Start });
        MonitoringTarget target = MonitoringTargets.Find(scenario.State, id)!;
        int minutes = 0;
        foreach (Guid observer in target.Observers)
        {
            minutes++;
            MonitoringObservation fact = new(Guid.NewGuid(), id, observer, scenario.State.Monitoring.Nodes[observer], target.CheckGeneration,
                target.ConfigRevision, 1, scenario.Start.AddSeconds(10), 1, true, null, observer.ToString("N"), scenario.Start.AddMinutes(-minutes), true);
            scenario.Accept(MonitoringCommands.Record(scenario.State, observer, fact.StorageGeneration, [fact]));
        }

        // Act
        scenario.Accept(MonitoringCommands.Evaluate(scenario.State, target, scenario.A, scenario.State.Monitoring.Nodes[scenario.A],
            1, scenario.Start.AddSeconds(11), false, scenario.Start.AddSeconds(20)));
        MonitoringEvaluation result = scenario.State.Monitoring.Live[id];

        // Assert
        Assert.Equal(AvailabilityState.Available, result.Availability);
        Assert.Equal(CoverageState.Complete, result.Coverage);
        Assert.Equal(2, result.Metadata.Values.Select(item => item.StartedAtUtc).Distinct().Count());
        Assert.Equal(2, result.Metadata.Values.Select(item => item.EndpointName).Distinct().Count());
        Assert.Empty(scenario.State.Monitoring.Incidents);
    }

    [Fact]
    public void NodeIncidentsUseNodeRoutesAndReportedFaultIsDifferentFromNetworkTimeout()
    {
        // Arrange
        MonitoringScenario scenario = new();
        Guid nodeChat = Guid.NewGuid();
        scenario.Apply(scenario.Command(nodeChat, DeletedEntityKind.Chat, 0, new ConfigurationValue { Name = "nodes", TelegramChatId = 789 }));
        scenario.Apply(scenario.Command(Guid.NewGuid(), DeletedEntityKind.NodeChatRoute, 0, new ConfigurationValue
        {
            ParentId = scenario.C, ChatId = nodeChat, Generation = Guid.NewGuid()
        }));
        MonitoringTarget target = MonitoringTargets.Find(scenario.State, scenario.C)!;
        foreach (Guid observer in target.Observers)
        {
            bool succeeded = observer == scenario.B;
            string? failure = null;
            if (!succeeded)
            {
                failure = "DatabaseUnavailable";
            }

            MonitoringObservation fact = new(Guid.NewGuid(), target.Id, observer, scenario.State.Monitoring.Nodes[observer], target.CheckGeneration,
                target.ConfigRevision, 1, scenario.Start, 1, succeeded, failure, null, null, true);
            scenario.Accept(MonitoringCommands.Record(scenario.State, observer, fact.StorageGeneration, [fact]));
        }

        // Act
        scenario.Accept(MonitoringCommands.Evaluate(scenario.State, target, scenario.A, scenario.State.Monitoring.Nodes[scenario.A],
            1, scenario.Start.AddSeconds(1), false, scenario.Start.AddSeconds(1)));

        // Assert
        MonitoringIncident incident = Assert.Single(scenario.State.Monitoring.Incidents.Values);
        Assert.Equal(IncidentKind.Node, incident.Kind);
        Assert.Equal(scenario.C, incident.TargetId);
        Assert.Equal(nodeChat, Assert.Single(scenario.State.Monitoring.Intents.Values).ChatId);
        Assert.Equal("DatabaseUnavailable", scenario.State.Monitoring.Live[target.Id].Failures[scenario.A]);
        Assert.Contains("DatabaseUnavailable", Assert.Single(scenario.State.Monitoring.Events.Values).Payload);
    }

    [Fact]
    public void HistoricalCommandRetryKeepsDigestAndDiagnosticsCannotIgnoreUnobservedTargets()
    {
        // Arrange
        MonitoringScenario scenario = new();
        MonitoringTarget target = MonitoringTargets.Find(scenario.State, scenario.TargetId)!;
        RevisionStateStore state = new();
        state.Publish(scenario.State);

        // Act
        RevisionCommand first = MonitoringCommands.Evaluate(scenario.State, target, scenario.A, scenario.State.Monitoring.Nodes[scenario.A],
            1, scenario.Start, true, scenario.Start.AddDays(1));
        RevisionCommand retry = MonitoringCommands.Evaluate(scenario.State, target, scenario.A, scenario.State.Monitoring.Nodes[scenario.A],
            1, scenario.Start, true, scenario.Start.AddDays(2));
        MonitoringProgress progress = MonitoringDiagnostics.Read(state, true, scenario.Start);

        // Assert
        Assert.Equal(RevisionCodec.Digest(first), RevisionCodec.Digest(retry));
        Assert.False(progress.CaughtUp);
        Assert.True(progress.HistoricalAssemblies > 0);
    }

    private static void CompleteTarget(MonitoringScenario scenario, Guid id)
    {
        MonitoringTarget target = MonitoringTargets.Find(scenario.State, id)!;
        for (int sequence = 1; sequence <= 4; sequence++)
        {
            foreach (Guid observer in target.Observers)
            {
                bool success = sequence == 4 || observer == scenario.B;
                string? failure = null;
                string? name = "app";
                if (!success)
                {
                    failure = "Timeout";
                    name = null;
                }

                MonitoringObservation observation = new(Guid.NewGuid(), id, observer, scenario.State.Monitoring.Nodes[observer], target.CheckGeneration,
                    target.ConfigRevision, sequence, scenario.Start.AddMinutes(sequence), 1, success, failure, name, null, true);
                scenario.Accept(MonitoringCommands.Record(scenario.State, observer, observation.StorageGeneration, [observation]));
            }

            scenario.Accept(MonitoringCommands.Evaluate(scenario.State, target, scenario.A, scenario.State.Monitoring.Nodes[scenario.A],
                1, scenario.Start.AddMinutes(sequence), true, scenario.Start.AddMinutes(sequence)));
        }
    }
}
