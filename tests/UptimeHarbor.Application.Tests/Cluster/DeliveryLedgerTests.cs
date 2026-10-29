using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class DeliveryLedgerTests
{
    [Fact]
    public void QueueAndActualAttemptsSurviveReplayAndKnownSuccessDoesNotRestart()
    {
        // Arrange
        DeliveryScenario scenario = new();
        RevisionImage initial = scenario.State;
        RevisionCommand begin = scenario.Begin();
        scenario.Accept(begin);
        RevisionCommand result = scenario.Result(begin.Delivery!.AttemptId);

        // Act
        scenario.Accept(result);
        RevisionImage committed = scenario.State;
        scenario.Apply(result, 2);
        RevisionImage replay = scenario.Reducer.Apply(initial, begin, initial.Index + 1, 1);
        replay = scenario.Reducer.Apply(replay, result, replay.Index + 1, 1);
        DeliveryQueue.Validate(replay);
        DeliveryIntent projected = DeliveryProjection.Intent(replay, replay.Monitoring.Intents[scenario.Problem.Id]);

        // Assert
        Assert.Equal(DeliveryState.Succeeded, projected.State);
        Assert.Equal(1, projected.AttemptCount);
        Assert.Single(replay.Monitoring.Attempts);
        Assert.Single(replay.Monitoring.ConfirmedDeliveries);
        Assert.Empty(DeliveryQueue.Due(replay, scenario.Now.AddYears(2), 100));
        Assert.Equal(committed.Monitoring, scenario.State.Monitoring);
        Assert.Equal(OperationOutcome.Rejected, scenario.Accept(scenario.Begin(), 2).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnbindBeforeCallOrLateSuccessRetainsOnlyEarnedRecovery(bool started)
    {
        // Arrange
        DeliveryScenario scenario = new();
        Guid? attempt = null;
        if (started)
        {
            attempt = scenario.StartAttempt();
        }

        AcceptedValue binding = scenario.State.Values.Values.Single(item => item.Kind == DeletedEntityKind.Binding);

        // Act
        Assert.Equal(OperationOutcome.Committed, scenario.Remove(binding.Id).Outcome);
        scenario.Recover();
        if (attempt.HasValue)
        {
            scenario.Accept(scenario.Result(attempt.Value));
        }

        // Assert
        DeliveryQueue.Validate(scenario.State);
        int recoveries = 0;
        if (started)
        {
            recoveries = 1;
        }

        Assert.Equal(recoveries, scenario.State.Monitoring.Intents.Values.Count(item => item.Kind == DeliveryKind.Recovery));
        if (started)
        {
            MonitoringIntent recovery = scenario.State.Monitoring.Intents.Values.Single(item => item.Kind == DeliveryKind.Recovery);
            Assert.Contains(recovery, DeliveryQueue.Due(scenario.State, scenario.Now, 100));

            // Act
            Guid recoveryAttempt = scenario.StartAttempt(recovery);
            scenario.Accept(scenario.Result(recoveryAttempt));
            scenario.Accept(scenario.Result(attempt!.Value), 2);

            // Assert
            Assert.Single(scenario.State.Monitoring.Intents.Values, item => item.Kind == DeliveryKind.Recovery);
            Assert.Empty(DeliveryQueue.Due(scenario.State, scenario.Now.AddDays(1), 100));
        }
    }

    [Theory]
    [InlineData("delete-target")]
    [InlineData("delete-chat")]
    [InlineData("endpoint")]
    [InlineData("reset")]
    [InlineData("disable")]
    public void ConfigurationCancellationBlocksClaimAndLateSuccessCannotReviveRecovery(string action)
    {
        // Arrange
        DeliveryScenario scenario = new();
        Guid attempt = scenario.StartAttempt();
        AcceptedValue cell = scenario.State.Values[scenario.TargetId];

        // Act
        if (action == "delete-chat")
        {
            Assert.Equal(OperationOutcome.Committed, scenario.Remove(scenario.ChatId).Outcome);
        }
        else if (action == "delete-target")
        {
            Assert.Equal(OperationOutcome.Committed, scenario.Remove(scenario.TargetId).Outcome);
        }
        else
        {
            ConfigurationValue value = cell.Value with { Generation = Guid.NewGuid() };
            if (action == "endpoint")
            {
                value = value with { Endpoint = "https://test.invalid/new" };
            }
            else if (action == "disable")
            {
                value = value with { Enabled = false };
            }
            else
            {
                value = value with { FailureThreshold = cell.Value.FailureThreshold + 1 };
            }

            Assert.Equal(OperationOutcome.Committed, scenario.Accept(scenario.Command(cell.Id, cell.Kind, cell.Revision, value)
                with { CreatedAtUtc = scenario.Now }).Outcome);
        }

        scenario.Accept(scenario.Result(attempt));

        // Assert
        Assert.Equal(DeliveryState.Succeeded, scenario.Problem.State);
        Assert.False(DeliveryEligibility.Allows(scenario.State, scenario.Problem));
        Assert.Empty(DeliveryQueue.Due(scenario.State, scenario.Now.AddDays(1), 100));
        Assert.DoesNotContain(scenario.State.Monitoring.Intents.Values, item => item.Kind == DeliveryKind.Recovery);
        DeliveryQueue.Validate(scenario.State);
    }

    [Fact]
    public void RetryAfterAndExhaustionKeepActualHistoryAndCannotBeRestartedByRebind()
    {
        // Arrange
        DeliveryScenario scenario = new();

        // Act
        foreach (int number in Enumerable.Range(1, 5))
        {
            Guid attempt = scenario.StartAttempt();
            scenario.Accept(scenario.Result(attempt, AttemptOutcome.Failed, "RateLimited/600"));
            if (number < 5)
            {
                Assert.Empty(DeliveryQueue.Due(scenario.State, scenario.Now.AddSeconds(599), 100));
                Assert.Equal(scenario.Now.AddSeconds(600), scenario.Problem.NextAttemptAtUtc);
                scenario.Now = scenario.Now.AddSeconds(600);
            }
        }

        AcceptedValue binding = scenario.State.Values.Values.Single(item => item.Kind == DeletedEntityKind.Binding);
        Assert.Equal(OperationOutcome.Committed, scenario.Remove(binding.Id).Outcome);
        scenario.Accept(scenario.Command(Guid.NewGuid(), DeletedEntityKind.Binding, 0, binding.Value));

        // Assert
        Assert.Equal(DeliveryState.Failed, scenario.Problem.State);
        Assert.Equal(5, scenario.State.Monitoring.Attempts.Count);
        Assert.Empty(DeliveryQueue.Due(scenario.State, scenario.Now.AddYears(1), 100));
        Assert.Equal(OperationOutcome.Rejected, scenario.Accept(scenario.Begin()).Outcome);
        Assert.Equal(5, DeliveryProjection.Intent(scenario.State, scenario.Problem).AttemptCount);
        DeliveryQueue.Validate(scenario.State);
    }

    [Fact]
    public void AmbiguousOldAttemptCanSucceedAfterFailoverAndNewClaimWithoutLosingHistory()
    {
        // Arrange
        DeliveryScenario scenario = new();
        Guid first = scenario.StartAttempt();
        scenario.Now = scenario.Now.AddSeconds(31);
        SenderAuthorityKey key = scenario.Key() with { Term = 2 };
        RevisionCommand abandon = DeliveryCommands.Create(scenario.State, scenario.A, scenario.State.Monitoring.Nodes[scenario.A],
            "AbandonDelivery", scenario.Problem.Id, Guid.NewGuid(), scenario.Now,
            new DeliveryCommand { AttemptId = first, Authority = key });

        // Act
        scenario.Accept(abandon, 2);
        scenario.Now = scenario.Now.AddSeconds(5);
        Guid second = scenario.StartAttempt();
        scenario.Accept(scenario.Result(first), 2);
        scenario.Accept(scenario.Result(second, AttemptOutcome.Failed, "Network"), 2);

        // Assert
        Assert.Equal(DeliveryState.Succeeded, scenario.Problem.State);
        Assert.Equal(2, DeliveryProjection.Intent(scenario.State, scenario.Problem).AttemptCount);
        Assert.Single(scenario.State.Monitoring.ConfirmedDeliveries);
        Assert.Empty(DeliveryQueue.Due(scenario.State, scenario.Now.AddDays(1), 100));
        DeliveryQueue.Validate(scenario.State);
    }

    [Fact]
    public void TestDeliveryRequiresAcceptedAdministratorAgainAndBackgroundHasNoActor()
    {
        // Arrange
        DeliveryScenario scenario = new();
        RevisionCommand test = scenario.Command(scenario.ChatId, DeletedEntityKind.Chat, 0, null) with { Action = "TestDelivery" };

        // Act
        scenario.Accept(test);
        MonitoringIntent intent = scenario.State.Monitoring.Intents[test.OperationId];
        AcceptedValue actor = scenario.State.Values[scenario.Actor];
        RevisionCommand disable = scenario.Command(actor.Id, actor.Kind, actor.Revision, actor.Value with { Enabled = false })
            with { ActorUserId = scenario.OtherActor, ActorRevision = scenario.State.Values[scenario.OtherActor].Revision };
        scenario.Accept(disable);

        // Assert
        Assert.False(DeliveryEligibility.Allows(scenario.State, intent));
        Assert.Equal(OperationOutcome.Rejected, scenario.Accept(scenario.Begin(intent)).Outcome);
        Assert.Equal(OperationOutcome.Rejected, scenario.Accept(test with { OperationId = Guid.NewGuid() }).Outcome);
        Assert.True(DeliveryEligibility.Allows(scenario.State, scenario.Problem));
        Assert.Null(scenario.Problem.ActorUserId);
        Assert.NotEmpty(DeliveryQueue.Due(scenario.State, scenario.Now, 100));
    }

    [Fact]
    public void QueueRejectsAttemptedTestStateWithoutActualAttemptHistory()
    {
        // Arrange
        DeliveryScenario scenario = new();
        RevisionCommand command = scenario.Command(scenario.ChatId, DeletedEntityKind.Chat, 0, null) with { Action = "TestDelivery" };
        scenario.Accept(command);
        MonitoringIntent intent = scenario.State.Monitoring.Intents[command.OperationId];
        RevisionImage corrupted = scenario.State with { Monitoring = scenario.State.Monitoring with
        {
            Intents = scenario.State.Monitoring.Intents.SetItem(intent.Id, intent with { State = DeliveryState.AttemptStarted })
        } };

        // Act / Assert
        Assert.Throws<InvalidDataException>(() => DeliveryQueue.Validate(corrupted));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryMessageDistinguishesPartialAndFullAndPreservesMetadataSources(bool full)
    {
        // Arrange
        DeliveryScenario scenario = new();
        scenario.ConfirmProblem(scenario.ChatId);
        if (full)
        {
            foreach (int second in new[] { 123, 124, 125 })
            {
                scenario.Observe(scenario.B, second, false);
            }

            scenario.Evaluate(126);
        }

        scenario.Recover();
        MonitoringIntent recovery = scenario.State.Monitoring.Intents.Values.Single(item => item.Kind == DeliveryKind.Recovery);

        // Act
        string message = DeliveryMessage.Format(scenario.State, recovery);

        // Assert
        if (full)
        {
            Assert.Contains("после полной аварии", message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Частичная недоступность устранена", message, StringComparison.Ordinal);
        }

        Assert.Contains($"Узел {scenario.A:N}: source-a", message, StringComparison.Ordinal);
        Assert.Contains($"Узел {scenario.B:N}: source-b", message, StringComparison.Ordinal);
        Assert.Contains($"Восстановлено направление: {scenario.A:N}", message, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarySuccessKeepsImmutableSourcesAndReplayCannotSendSources()
    {
        // Arrange
        DeliveryScenario scenario = new();
        scenario.Recover();
        scenario.Accept(scenario.Backlog(1));
        MonitoringSummary summary = Assert.Single(scenario.State.Monitoring.Summaries.Values);
        MonitoringIntent intent = scenario.State.Monitoring.Intents[summary.IntentId];

        // Act
        Guid attempt = scenario.StartAttempt(intent);
        RevisionCommand result = scenario.Result(attempt);
        scenario.Accept(result);
        scenario.Apply(result, 2);

        // Assert
        Assert.Empty(DeliveryQueue.Due(scenario.State, scenario.Now.AddDays(1), 100));
        Assert.Equal(summary, scenario.State.Monitoring.Summaries[summary.Id]);
        Assert.All(summary.SourceIntentIds, id => Assert.Equal(DeliveryState.SuppressedBySummary, scenario.State.Monitoring.Intents[id].State));
        Assert.Equal(2, scenario.State.Monitoring.Events.Count);
        Assert.Single(scenario.State.Monitoring.Attempts);
        DeliveryQueue.Validate(scenario.State);
    }
}
