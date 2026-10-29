using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class AcceptedDeliveryTests
{
    [Fact]
    public async Task ReconstructedQueueSendsOnceAndRestartsWithoutLocalOutboxOrSession()
    {
        // Arrange
        using DeliveryHarness harness = new();
        await harness.PrepareAsync(test: false);

        // Act
        await harness.Delivery.RunOnceAsync(TestContext.Current.CancellationToken);
        await harness.CreateDispatcher().RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([harness.ChatId], harness.Sent);
        Assert.Equal(DeliveryState.Succeeded, Assert.Single(harness.Image.Monitoring.Intents.Values).State);
        Assert.Single(harness.Image.Monitoring.Attempts);
        Assert.Single(harness.Audit, item => item.Outcome == "Succeeded");
        Assert.Single(harness.Local.Saved);
        Assert.Null(Assert.Single(harness.Image.Monitoring.Intents.Values).ActorUserId);
        DeliveryQueue.Validate(harness.Image);
    }

    [Theory]
    [InlineData("majority")]
    [InlineData("term")]
    [InlineData("pause")]
    [InlineData("delete")]
    [InlineData("disabled-actor")]
    public async Task AppliedChangeAfterClaimPreventsExternalCall(string change)
    {
        // Arrange
        using DeliveryHarness harness = new();
        await harness.PrepareAsync();
        harness.AfterBegin = async _ =>
        {
            if (change == "majority")
            {
                harness.Source.Consensus = harness.Source.Consensus! with { ConsensusToken = new CancellationToken(true) };
            }
            else if (change == "term")
            {
                harness.Source.Consensus = harness.Source.Consensus! with { Term = 2 };
            }
            else if (change == "pause")
            {
                harness.Source.Time.Now = harness.Source.Time.Now.AddSeconds(10);
            }
            else if (change == "delete")
            {
                RevisionCommand delete = harness.Command(harness.ChatId, DeletedEntityKind.Chat, null) with { Action = "Delete" };
                await harness.Source.Dispatcher.AcceptAsync(delete, harness.Source.Options.NodeId, CancellationToken.None);
            }
            else
            {
                AcceptedValue actor = harness.Image.Values[harness.Actor];
                Guid other = harness.Image.Values.Values.Single(item => item.Kind == DeletedEntityKind.Account && item.Id != actor.Id).Id;
                RevisionCommand disable = harness.Command(actor.Id, actor.Kind, actor.Value with { Enabled = false })
                    with { ActorUserId = other, ActorRevision = harness.Image.Values[other].Revision };
                await harness.Source.Dispatcher.AcceptAsync(disable, harness.Source.Options.NodeId, CancellationToken.None);
            }
        };

        // Act
        await harness.Delivery.RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(harness.Sent);
        Assert.Single(harness.Image.Monitoring.Attempts);
        Assert.DoesNotContain(harness.Image.Monitoring.Attempts.Values, item => item.Outcome == AttemptOutcome.Succeeded);
        Assert.DoesNotContain(harness.Audit, item => item.Outcome == "Succeeded");
    }

    [Theory]
    [InlineData("follower")]
    [InlineData("recovery")]
    [InlineData("delivery")]
    [InlineData("authority")]
    public async Task UnknownDependenciesKeepGateClosedBeforeAttempt(string dependency)
    {
        // Arrange
        using DeliveryHarness harness = new();
        await harness.PrepareAsync();
        if (dependency == "follower")
        {
            harness.Source.Consensus = harness.Source.Consensus! with { IsLeader = false };
        }
        else if (dependency == "recovery")
        {
            harness.Source.State.Invalidate();
        }
        else if (dependency == "delivery")
        {
            harness.Source.Accepted = harness.Source.Accepted with { DeliveryStateConfirmed = false };
        }
        else
        {
            harness.Gate.Close();
        }

        // Act
        await harness.Delivery.RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(harness.Sent);
        Assert.Empty(harness.Image.Monitoring.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalSuccessBeforeAcceptedResultIsDurablyForwardedAfterRestart(bool commitResponseLost)
    {
        // Arrange
        using DeliveryHarness harness = new();
        await harness.PrepareAsync();
        harness.RejectResults = !commitResponseLost;
        harness.Local.LoseCommitResponse = commitResponseLost;

        // Act
        await harness.Delivery.RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(harness.Sent);
        Assert.Equal(DeliveryState.AttemptStarted, Assert.Single(harness.Image.Monitoring.Intents.Values).State);
        Assert.Single(harness.Local.Saved);

        // Act
        harness.RejectResults = false;
        harness.Local.LoseCommitResponse = false;
        await harness.CreateDispatcher().RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(harness.Sent);
        Assert.Equal(DeliveryState.Succeeded, Assert.Single(harness.Image.Monitoring.Intents.Values).State);
        Assert.Single(harness.Audit, item => item.Outcome == "Succeeded");
    }

    [Fact]
    public async Task AcceptedResultWithLostResponseDoesNotRepeatTransportOrSuccessAudit()
    {
        // Arrange
        using DeliveryHarness harness = new();
        await harness.PrepareAsync();
        harness.LoseResultResponse = true;

        // Act
        await harness.Delivery.RunOnceAsync(TestContext.Current.CancellationToken);
        harness.LoseResultResponse = false;
        await harness.CreateDispatcher().RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(harness.Sent);
        Assert.Single(harness.Image.Monitoring.Attempts);
        Assert.Single(harness.Audit, item => item.Outcome == "Succeeded");
        Assert.Equal(DeliveryState.Succeeded, Assert.Single(harness.Image.Monitoring.Intents.Values).State);
    }

    [Fact]
    public async Task AmbiguousTimeoutRetainsIntentAndRetriesWithoutFabricatedSuccess()
    {
        // Arrange
        using DeliveryHarness harness = new();
        await harness.PrepareAsync();
        harness.Send = _ => throw new NotificationDeliveryException(NotificationDeliveryFailure.Timeout);

        // Act
        await harness.Delivery.RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(DeliveryState.RetryDue, Assert.Single(harness.Image.Monitoring.Intents.Values).State);
        Assert.Empty(harness.Image.Monitoring.ConfirmedDeliveries);
        Assert.DoesNotContain(harness.Audit, item => item.Outcome == "Succeeded");

        // Arrange
        foreach (int _ in Enumerable.Range(1, 6))
        {
            harness.Source.Time.Now = harness.Source.Time.Now.AddSeconds(1);
            harness.Gate.Tick();
        }

        harness.Send = null;
        Assert.True(await harness.Gate.RenewAsync(TestContext.Current.CancellationToken));

        // Act
        await harness.CreateDispatcher().RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, harness.Sent.Count);
        Assert.Equal(2, harness.Image.Monitoring.Attempts.Count);
        Assert.Single(harness.Image.Monitoring.ConfirmedDeliveries);
        DeliveryQueue.Validate(harness.Image);
    }

    [Fact]
    public async Task InFlightOutageSuccessAfterUnbindAndRecoveryEarnsOneFinalRecovery()
    {
        // Arrange
        using DeliveryHarness harness = new();
        await harness.PrepareAsync(test: false);
        harness.Send = async _ =>
        {
            harness.Send = null;
            AcceptedValue binding = harness.Image.Values.Values.Single(item => item.Kind == DeletedEntityKind.Binding);
            await harness.Source.Dispatcher.AcceptAsync(harness.Command(binding.Id, binding.Kind, null) with { Action = "Delete" },
                harness.Source.Options.NodeId, TestContext.Current.CancellationToken);
            MonitoringTarget target = MonitoringTargets.Find(harness.Image, binding.Value.ParentId!.Value)!;
            MonitoringObservation success = new(Guid.NewGuid(), target.Id, harness.Source.Options.NodeId,
                harness.Source.Options.StorageGeneration, target.CheckGeneration, target.ConfigRevision, 4,
                harness.Source.Time.Now, 1, true, null, "source", null, true);
            await harness.Source.Dispatcher.AcceptAsync(MonitoringCommands.Record(harness.Image, harness.Source.Options.NodeId,
                harness.Source.Options.StorageGeneration, [success]), harness.Source.Options.NodeId, TestContext.Current.CancellationToken);
            await harness.Source.Dispatcher.AcceptAsync(MonitoringCommands.Evaluate(harness.Image, target, harness.Source.Options.NodeId,
                harness.Source.Options.StorageGeneration, 1, harness.Source.Time.Now, false, harness.Source.Time.Now),
                harness.Source.Options.NodeId, TestContext.Current.CancellationToken);
            return 42;
        };

        // Act
        await harness.Delivery.RunOnceAsync(TestContext.Current.CancellationToken);
        await harness.CreateDispatcher().RunOnceAsync(TestContext.Current.CancellationToken);
        await harness.Delivery.RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, harness.Sent.Count);
        Assert.Single(harness.Image.Monitoring.Intents.Values, item => item.Kind == DeliveryKind.Recovery && item.State == DeliveryState.Succeeded);
        Assert.Equal(2, harness.Image.Monitoring.ConfirmedDeliveries.Count);
        DeliveryQueue.Validate(harness.Image);
    }

    [Fact]
    public async Task SlowChatAndForbiddenChatDoNotBlockOtherRecipientsOrCauseResign()
    {
        // Arrange
        using DeliveryHarness harness = new();
        await harness.PrepareAsync();
        Guid other = Guid.NewGuid();
        await harness.ApplyAsync(other, DeletedEntityKind.Chat, new ConfigurationValue { Name = "other", TelegramChatId = 456 });
        await harness.Dispatcher.AcceptAsync(harness.Command(other, DeletedEntityKind.Chat, null) with { Action = "TestDelivery", BaseRevision = 0 },
            harness.Source.Options.NodeId, CancellationToken.None);
        TaskCompletionSource<long> slow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource otherCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Send = recipient =>
        {
            if (recipient.Id == harness.ChatId)
            {
                return slow.Task;
            }

            otherCalled.SetResult();
            throw new NotificationDeliveryException(NotificationDeliveryFailure.Forbidden);
        };

        // Act
        Task run = harness.Delivery.RunOnceAsync(TestContext.Current.CancellationToken);
        await otherCalled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        slow.SetResult(42);
        await run;

        // Assert
        Assert.Equal(2, harness.Sent.Count);
        Assert.Contains(harness.Image.Monitoring.Intents.Values, item => item.ChatId == harness.ChatId && item.State == DeliveryState.Succeeded);
        Assert.Contains(harness.Image.Monitoring.Intents.Values, item => item.ChatId == other && item.State == DeliveryState.Failed);
        Assert.True(harness.Source.Consensus!.IsLeader);
        DeliveryQueue.Validate(harness.Image);
    }
}
