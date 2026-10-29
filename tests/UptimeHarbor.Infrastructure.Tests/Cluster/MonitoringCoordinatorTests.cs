using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class MonitoringCoordinatorTests
{
    [Fact]
    public async Task LateOfflineHistoryInSameTermCreatesAnotherDurableSummaryAndRetainsSources()
    {
        // Arrange
        using MonitoringHarness runtime = new();
        AcceptedValue target = runtime.State.Current!.Values.Values.Single(item => item.Kind == DeletedEntityKind.Server);
        Guid chatId = Guid.NewGuid();
        await Configure(runtime, chatId, DeletedEntityKind.Chat, new() { Name = "chat", TelegramChatId = 123 });
        await Configure(runtime, Guid.NewGuid(), DeletedEntityKind.Binding, new()
        {
            ParentId = target.Id, ChatId = chatId, Generation = Guid.NewGuid(), Enabled = true
        });
        runtime.Time.Now = DateTimeOffset.UnixEpoch.AddYears(2);
        runtime.Readiness.Report("Database", true, "Ready", TimeSpan.FromDays(1000));
        runtime.Readiness.Report("Storage", true, "Ready", TimeSpan.FromDays(1000));
        runtime.Readiness.Report("Clock", true, "Ready", TimeSpan.FromDays(1000));
        MonitoringCoordinator coordinator = new(runtime.State, runtime, runtime.Readiness, runtime.Dispatcher, Options.Create(runtime.Options), runtime.Time);
        await Append(runtime, target, 1, false);
        await Append(runtime, target, 2, false);
        await Append(runtime, target, 3, false);
        await Append(runtime, target, 4, true);
        runtime.Accepted = runtime.Accepted with { DeliveryStateConfirmed = false };

        // Act
        await coordinator.RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(runtime.State.Current!.Monitoring.Summaries);
        Assert.Single(runtime.State.Current.Monitoring.Incidents.Values, item => item.Kind == IncidentKind.Target && item.Completion == IncidentCompletion.Recovered);

        // Act
        runtime.Accepted = runtime.Accepted with { DeliveryStateConfirmed = true };
        await coordinator.RunOnceAsync(TestContext.Current.CancellationToken);
        await Append(runtime, target, 5, false);
        await Append(runtime, target, 6, false);
        await Append(runtime, target, 7, false);
        await Append(runtime, target, 8, true);
        await coordinator.RunOnceAsync(TestContext.Current.CancellationToken);
        var summaries = runtime.State.Current!.Monitoring.Summaries.Values.ToArray();
        await coordinator.RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, summaries.Length);
        Assert.Equal(2, runtime.State.Current.Monitoring.Summaries.Count);
        Assert.All(summaries.SelectMany(item => item.SourceIntentIds), id =>
            Assert.Equal(DeliveryState.SuppressedBySummary, runtime.State.Current.Monitoring.Intents[id].State));
        Assert.DoesNotContain(runtime.State.Current.Monitoring.Intents.Values, item => item.Kind == DeliveryKind.Recovery);
        Assert.All(summaries, item => Assert.True(item.CutIndex < item.Index));
    }

    [Fact]
    public async Task HistoricalBudgetDoesNotJumpToLiveOrAuthorizeReturnedFollower()
    {
        // Arrange
        using MonitoringHarness runtime = new();
        AcceptedValue target = runtime.State.Current!.Values.Values.Single(item => item.Kind == DeletedEntityKind.Server);
        runtime.Time.Now = DateTimeOffset.UnixEpoch.AddMinutes(30);
        for (int sequence = 1; sequence <= 15; sequence++)
        {
            await Append(runtime, target, sequence, true);
        }

        MonitoringCoordinator coordinator = new(runtime.State, runtime, runtime.Readiness, runtime.Dispatcher, Options.Create(runtime.Options), runtime.Time);

        // Act
        await coordinator.RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(10), runtime.State.Current!.Monitoring.History[target.Id].AtUtc);
        Assert.DoesNotContain(target.Id, runtime.State.Current.Monitoring.Live.Keys);
        Assert.False(MonitoringDiagnostics.Read(runtime.State, true, runtime.Time.Now).CaughtUp);

        // Act
        runtime.Consensus = runtime.Consensus! with { IsLeader = false, Term = 2 };
        long before = runtime.State.Current.Index;
        await coordinator.RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(before, runtime.State.Current.Index);

        // Act
        runtime.Consensus = runtime.Consensus with { IsLeader = true };
        await coordinator.RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(15), runtime.State.Current.Monitoring.History[target.Id].AtUtc);
        Assert.Equal(2, runtime.State.Current.Monitoring.Live[target.Id].Term);
        Assert.Equal(15, runtime.State.Current.Monitoring.Observations.Count);
    }

    private static Task<CommandResult> Configure(MonitoringHarness runtime, Guid id, DeletedEntityKind kind, ConfigurationValue value)
    {
        RevisionImage image = runtime.State.Current!;
        AcceptedValue actor = image.Values.Values.Single(item => item.Kind == DeletedEntityKind.Account);
        RevisionCommand command = new()
        {
            ClusterId = image.ClusterId, ClusterGeneration = image.Generation, OperationId = Guid.NewGuid(),
            OriginNodeId = runtime.Options.NodeId, ActorUserId = actor.Id, ActorRevision = actor.Revision,
            EntityId = id, EntityKind = kind, Action = "Upsert", BaseRevision = 0, CreatedAtUtc = runtime.Time.Now, Value = value
        };
        return runtime.Dispatcher.AcceptAsync(command, runtime.Options.NodeId, TestContext.Current.CancellationToken);
    }

    private static Task<CommandResult> Append(MonitoringHarness runtime, AcceptedValue target, long sequence, bool succeeded)
    {
        string? failure = null;
        string? name = "app";
        if (!succeeded)
        {
            failure = "Timeout";
            name = null;
        }

        MonitoringObservation observation = new(Guid.NewGuid(), target.Id, runtime.Options.NodeId, runtime.Options.StorageGeneration,
            target.Value.Generation, target.Revision, sequence, DateTimeOffset.UnixEpoch.AddMinutes(sequence), 1, succeeded, failure, name, null, true);
        return runtime.Dispatcher.AcceptAsync(MonitoringCommands.Record(runtime.State.Current!, runtime.Options.NodeId,
            runtime.Options.StorageGeneration, [observation]), runtime.Options.NodeId, TestContext.Current.CancellationToken);
    }
}
