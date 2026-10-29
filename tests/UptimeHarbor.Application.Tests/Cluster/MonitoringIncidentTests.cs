using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class MonitoringIncidentTests
{
    [Fact]
    public void PartialFullAndRecoveryKeepOneIncidentAcrossTermAndRepeat()
    {
        // Arrange
        MonitoringScenario scenario = new();
        foreach (int second in new[] { 1, 60, 120 })
        {
            scenario.Observe(scenario.A, second, false);
            scenario.Observe(scenario.B, second, true);
        }

        scenario.Evaluate(121);
        Guid incidentId = scenario.State.Monitoring.Incidents.Values.Single().Id;
        int events = scenario.State.Monitoring.Events.Count;

        // Act
        scenario.Evaluate(122);
        foreach (int second in new[] { 180, 240, 300 })
        {
            scenario.Observe(scenario.A, second, false);
            scenario.Observe(scenario.B, second, false);
        }

        scenario.Evaluate(301, term: 2);
        scenario.ConfirmProblem(scenario.ChatId);
        scenario.Record(scenario.Observation(scenario.A, 302, true));
        scenario.Record(scenario.Observation(scenario.B, 302, true));
        RevisionImage beforeRecovery = scenario.State;
        scenario.Evaluate(303, term: 2);
        RevisionCommand recovery = scenario.Journal[^1];
        RevisionImage committed = scenario.State;
        scenario.Apply(recovery, 3);

        // Assert
        Assert.Single(scenario.State.Monitoring.Incidents);
        Assert.Equal(events + 2, scenario.State.Monitoring.Events.Count);
        Assert.Equal(IncidentCompletion.Recovered, scenario.State.Monitoring.Incidents[incidentId].Completion);
        Assert.Single(scenario.State.Monitoring.Intents.Values, item => item.Kind == DeliveryKind.Recovery);
        Assert.Equal(committed.Monitoring, scenario.State.Monitoring);
        var writes = RevisionWriteSetFactory.Create(beforeRecovery, committed, scenario.A).Records;
        Assert.Single(writes.OfType<SharedIncident>());
        Assert.Single(writes.OfType<IncidentEvent>());
        Assert.Single(writes.OfType<DeliveryIntent>());
        Assert.Single(writes.OfType<RecoveryEntitlement>());
    }

    [Fact]
    public void RecoveryUsesAcceptedEvidenceAfterUnbindAndDeletionStillWins()
    {
        // Arrange
        MonitoringScenario scenario = new();
        Guid secondChat = Guid.NewGuid();
        scenario.Apply(scenario.Command(secondChat, DeletedEntityKind.Chat, 0, new ConfigurationValue { Name = "other", TelegramChatId = 456 }));
        scenario.Apply(scenario.Command(Guid.NewGuid(), DeletedEntityKind.Binding, 0,
            new ConfigurationValue { ParentId = scenario.TargetId, ChatId = secondChat, Generation = Guid.NewGuid() }));
        foreach (int second in new[] { 1, 60, 120 })
        {
            scenario.Observe(scenario.A, second, false);
            scenario.Observe(scenario.B, second, true);
        }

        scenario.Evaluate(121);
        scenario.ConfirmProblem(scenario.ChatId);
        AcceptedValue binding = scenario.State.Values.Values.Single(item => item.Kind == DeletedEntityKind.Binding && item.Value.ChatId == scenario.ChatId);

        // Act
        Assert.Equal(OperationOutcome.Committed, scenario.Apply(scenario.Command(binding.Id, binding.Kind, binding.Revision, null) with { Action = "Delete" }).Outcome);
        scenario.Observe(scenario.A, 182, true);
        scenario.Observe(scenario.B, 182, true);
        scenario.Evaluate(183);

        // Assert
        MonitoringIntent recovery = Assert.Single(scenario.State.Monitoring.Intents.Values, item => item.Kind == DeliveryKind.Recovery);
        Assert.Equal(scenario.ChatId, recovery.ChatId);
        Assert.DoesNotContain(scenario.State.Monitoring.Intents.Values, item => item.ChatId == secondChat && item.Kind == DeliveryKind.Recovery);

        // Act
        AcceptedValue chat = scenario.State.Values[scenario.ChatId];
        scenario.Apply(scenario.Command(chat.Id, chat.Kind, chat.Revision, null) with { Action = "Delete" });

        // Assert
        Assert.Equal(DeliveryState.Cancelled, scenario.State.Monitoring.Intents[recovery.Id].State);
    }

    [Fact]
    public void HistoricalRangesAndOldCompletedBacklogProduceStableSummaryWithoutAgeCutoff()
    {
        // Arrange
        MonitoringScenario scenario = new();
        foreach (int second in new[] { 1, 60, 120 })
        {
            scenario.Observe(scenario.A, second, false);
        }

        CommandResult incomplete = scenario.Evaluate(120, historical: true);
        foreach (int second in new[] { 1, 60, 120 })
        {
            scenario.Observe(scenario.B, second, false);
        }

        scenario.Evaluate(120, historical: true);
        scenario.Observe(scenario.A, 180, true);
        scenario.Observe(scenario.B, 180, true);
        scenario.Evaluate(180, historical: true);
        RevisionCommand plan = scenario.Backlog();

        // Act
        scenario.Accept(plan, 2);
        MonitoringImage composed = scenario.State.Monitoring;
        scenario.Apply(plan, 3);

        // Assert
        Assert.Equal("HistoricalRangeIncomplete", incomplete.Reason);
        MonitoringSummary summary = Assert.Single(scenario.State.Monitoring.Summaries.Values);
        Assert.NotEmpty(summary.SourceIntentIds);
        Assert.All(summary.SourceIntentIds, id => Assert.Equal(DeliveryState.SuppressedBySummary, scenario.State.Monitoring.Intents[id].State));
        Assert.True(summary.Text.Length <= 3500);
        Assert.Contains("2026-01-01", summary.Text);
        Assert.Equal(composed, scenario.State.Monitoring);
        Assert.DoesNotContain(scenario.State.Monitoring.Intents.Values, item => item.Kind == DeliveryKind.Recovery);
        Assert.All(scenario.State.Monitoring.Incidents.Values, item => Assert.Equal(IncidentCompletion.Recovered, item.Completion));

        // Act
        AcceptedValue binding = scenario.State.Values.Values.Single(item => item.Kind == DeletedEntityKind.Binding && !item.Deleted);
        CommandResult unbound = scenario.Accept(scenario.Command(binding.Id, binding.Kind, binding.Revision, null) with { Action = "Delete" }, 3);

        // Assert
        Assert.Equal(OperationOutcome.Committed, unbound.Outcome);
        Assert.Equal(DeliveryState.Cancelled, scenario.State.Monitoring.Intents[summary.IntentId].State);
        Assert.All(summary.SourceIntentIds, id => Assert.Equal(DeliveryState.Cancelled, scenario.State.Monitoring.Intents[id].State));
        Assert.Equal(summary, scenario.State.Monitoring.Summaries[summary.Id]);
        Assert.NotEmpty(scenario.State.Monitoring.Events);
    }

    [Fact]
    public void WrongStorageGenerationOrFakeAdministratorCannotAuthorizeSystemWork()
    {
        // Arrange
        MonitoringScenario scenario = new();
        RevisionCommand command = MonitoringCommands.Record(scenario.State, scenario.A, scenario.State.Monitoring.Nodes[scenario.A],
            [scenario.Observation(scenario.A, 1, true)]);
        RevisionCommand fakeActor = command with { ActorUserId = scenario.Actor, ActorRevision = 1 };
        RevisionCommand wrongStorage = command with
        {
            Monitoring = command.Monitoring! with { StorageGeneration = Guid.NewGuid(), Observations = command.Monitoring.Observations
                .Select(item => item with { StorageGeneration = Guid.NewGuid() }).ToImmutableArray() }
        };

        // Act
        Exception? actor = Record.Exception(() => scenario.Apply(fakeActor));
        Exception? mismatch = Record.Exception(() => scenario.Apply(wrongStorage));

        // Assert
        Assert.IsType<InvalidDataException>(actor);
        Assert.IsType<InvalidDataException>(mismatch);
        Assert.Empty(scenario.State.Monitoring.Observations);
    }
}
