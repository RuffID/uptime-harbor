using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class MonitoringRecoveryTests
{
    [Fact]
    public async Task SnapshotAndRealWalRestartKeepIncidentAndRecoverInOrderBeforeBacklog()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        using MonitoringHarness source = new();
        RevisionImage image = await Outage(source);
        Guid incidentId = Assert.Single(image.Monitoring.Incidents.Keys);
        source.Options.StoragePath = directory.Location;
        RevisionSnapshotStore snapshots = new(Options.Create(source.Options));
        var snapshot = await snapshots.PublishAsync(MonitoringSnapshotRows.Export(image), TestContext.Current.CancellationToken);
        RecoveryAdmission proof = new(image.ClusterId, image.Generation, source.Options.NodeId, source.Options.StorageGeneration,
            true, false, snapshot.Manifest.SnapshotId, snapshot.Digest, image.Index, 1) { MonitoringNodes = image.Monitoring.Nodes };
        MonitoringHarness.Projection projection = new() { Head = new(image.ClusterId, image.Generation, image.Index, image.Term, image.Digest) };
        AcceptedValue cell = image.Values.Values.Single(item => item.Kind == DeletedEntityKind.Server);
        MonitoringTarget target = MonitoringTargets.Find(image, cell.Id)!;
        MonitoringObservation success = new(Guid.NewGuid(), cell.Id, source.Options.NodeId, source.Options.StorageGeneration,
            target.CheckGeneration, target.ConfigRevision, 4, source.Time.Now.AddSeconds(1), 1, true, null, "restored", null, true);
        RevisionCommand record = MonitoringCommands.Record(image, source.Options.NodeId, source.Options.StorageGeneration, [success]);
        RevisionImage afterRecord = new RevisionReducer().Apply(image, record, image.Index + 1, 2);
        RevisionCommand evaluate = MonitoringCommands.Evaluate(afterRecord, target, source.Options.NodeId, source.Options.StorageGeneration,
            2, success.CheckedAtUtc, true, success.CheckedAtUtc);
        string walPath = Path.Combine(directory.Location, "wal");

        // Act
        var first = Create(proof, source, snapshots, projection);
        await first.Machine.RestoreAsync(source.Options, TestContext.Current.CancellationToken);
        await using (WriteAheadLog wal = new(WalOptions(walPath), first.Machine))
        {
            await wal.InitializeAsync(TestContext.Current.CancellationToken);
            foreach (RevisionCommand command in new[] { record, evaluate, evaluate })
            {
                long index = await wal.AppendAsync(new BinaryLogEntry { Term = 2, CommandId = RevisionCodec.COMMAND_ID,
                    Content = RevisionCodec.Encode(command) }, TestContext.Current.CancellationToken);
                await wal.CommitAsync(index, TestContext.Current.CancellationToken);
                await wal.WaitForApplyAsync(index, TestContext.Current.CancellationToken);
            }
        }

        var restarted = Create(proof, source, snapshots, projection);
        await restarted.Machine.RestoreAsync(source.Options, TestContext.Current.CancellationToken);
        await using WriteAheadLog reopened = new(WalOptions(walPath), restarted.Machine);
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.False(restarted.State.Available);

        // Act
        bool confirmed = await restarted.Recovery.ConfirmLocalReplayAsync(reopened.LastCommittedEntryIndex, reopened.LastAppliedIndex, 1,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.True(confirmed);
        MonitoringImage accepted = restarted.State.Current!.Monitoring;
        Assert.Equal(IncidentCompletion.Recovered, accepted.Incidents[incidentId].Completion);
        Assert.Equal(2, accepted.Events.Count);
        Assert.Equal([1L, 2L], accepted.Events.Values.OrderBy(item => item.Sequence).Select(item => item.Sequence));
        Assert.Equal(4, accepted.Observations.Count);
        Assert.Equal(3, projection.Writes.Count);
        Assert.Empty(accepted.ConfirmedDeliveries);
        Assert.DoesNotContain(accepted.Intents.Values, item => item.Kind == DeliveryKind.Recovery);
        Assert.False(restarted.Recovery.Accepted.Activated);
    }

    [Fact]
    public async Task RealWalReplaysActualAttemptsAndResultsWithoutRepeatingKnownSuccess()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        using DeliveryHarness delivery = new();
        await delivery.PrepareAsync();
        MonitoringHarness source = delivery.Source;
        RevisionImage image = delivery.Image;
        MonitoringIntent intent = Assert.Single(image.Monitoring.Intents.Values);
        source.Options.StoragePath = directory.Location;
        RevisionSnapshotStore snapshots = new(Options.Create(source.Options));
        var snapshot = await snapshots.PublishAsync(MonitoringSnapshotRows.Export(image), TestContext.Current.CancellationToken);
        RecoveryAdmission proof = new(image.ClusterId, image.Generation, source.Options.NodeId, source.Options.StorageGeneration,
            true, false, snapshot.Manifest.SnapshotId, snapshot.Digest, image.Index, 1) { MonitoringNodes = image.Monitoring.Nodes };
        MonitoringHarness.Projection projection = new() { Head = new(image.ClusterId, image.Generation, image.Index, image.Term, image.Digest) };
        Guid attemptId = DeliveryCommands.AttemptId(intent.Id, 1);
        SenderAuthorityKey key = new(image.ClusterId, image.Generation, 1, 2, source.Options.NodeId, Guid.NewGuid());
        RevisionCommand begin = DeliveryCommands.Create(image, source.Options.NodeId, source.Options.StorageGeneration,
            "BeginDelivery", intent.Id, Guid.NewGuid(), source.Time.Now, new DeliveryCommand { Authority = key, AttemptId = attemptId });
        RevisionCommand result = DeliveryCommands.Create(image, source.Options.NodeId, source.Options.StorageGeneration,
            "RecordDelivery", intent.Id, Guid.NewGuid(), source.Time.Now, new DeliveryCommand
            {
                AttemptId = attemptId, Outcome = AttemptOutcome.Succeeded, CompletedAtUtc = source.Time.Now, TelegramMessageId = 42
            }, transport: true);
        string walPath = Path.Combine(directory.Location, "wal");

        // Act
        var first = Create(proof, source, snapshots, projection);
        await first.Machine.RestoreAsync(source.Options, TestContext.Current.CancellationToken);
        await using (WriteAheadLog wal = new(WalOptions(walPath), first.Machine))
        {
            await wal.InitializeAsync(TestContext.Current.CancellationToken);
            foreach (RevisionCommand command in new[] { begin, result, result })
            {
                long index = await wal.AppendAsync(new BinaryLogEntry { Term = 2, CommandId = RevisionCodec.COMMAND_ID,
                    Content = RevisionCodec.Encode(command) }, TestContext.Current.CancellationToken);
                await wal.CommitAsync(index, TestContext.Current.CancellationToken);
                await wal.WaitForApplyAsync(index, TestContext.Current.CancellationToken);
            }
        }

        var restarted = Create(proof, source, snapshots, projection);
        await restarted.Machine.RestoreAsync(source.Options, TestContext.Current.CancellationToken);
        await using WriteAheadLog reopened = new(WalOptions(walPath), restarted.Machine);
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        bool confirmed = await restarted.Recovery.ConfirmLocalReplayAsync(reopened.LastCommittedEntryIndex,
            reopened.LastAppliedIndex, 1, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(confirmed);
        RevisionImage restored = restarted.State.Current!;
        DeliveryQueue.Validate(restored);
        Assert.Single(restored.Monitoring.Attempts);
        Assert.Single(restored.Monitoring.ConfirmedDeliveries);
        Assert.Equal(DeliveryState.Succeeded, restored.Monitoring.Intents[intent.Id].State);
        Assert.Empty(DeliveryQueue.Due(restored, source.Time.Now.AddYears(1), 100));
        using var context = RevisionFixture.CreateContext();
        SnapshotImageValidation.Validate(context, MonitoringSnapshotRows.Export(restored));
        Assert.False(restarted.Recovery.Accepted.DeliveryStateConfirmed);
        Assert.Equal(Guid.Empty, restarted.Recovery.Accepted.SenderEpoch);
    }

    [Fact]
    public async Task SnapshotValidatesMonitoringRowsAndRequiresRealAcceptedDeliveryEvidence()
    {
        // Arrange
        using MonitoringHarness source = new();
        RevisionImage image = await Outage(source);
        Guid incidentId = Assert.Single(image.Monitoring.Incidents.Keys);
        Guid chatId = Guid.NewGuid();
        Guid eventId = Assert.Single(image.Monitoring.Events.Keys);
        MonitoringIntent intent = new(Guid.NewGuid(), incidentId, eventId, chatId, DeliveryKind.Problem, Guid.NewGuid(),
            "test-accepted-problem", "safe payload", DeliveryState.Succeeded, null, source.Time.Now, image.Index, image.Term);
        MonitoringDeliveryEvidence evidence = new(intent.Id, DeliveryCommands.AttemptId(intent.Id, 1), image.Index, image.Term, 1, source.Options.NodeId,
            Guid.NewGuid(), source.Time.Now, source.Time.Now, 123);
        image = image with { Monitoring = image.Monitoring with
        {
            Intents = image.Monitoring.Intents.Add(intent.Id, intent), ConfirmedDeliveries = image.Monitoring.ConfirmedDeliveries.Add(intent.Id, evidence),
            Attempts = image.Monitoring.Attempts.Add(evidence.AttemptId, new AcceptedDeliveryAttempt(evidence.AttemptId,
                intent.Id, 1, new(image.ClusterId, image.Generation, 1, image.Term, evidence.SenderNodeId, evidence.SenderEpoch),
                evidence.StartedAtUtc, image.Index, image.Term)
            {
                Outcome = AttemptOutcome.Succeeded, CompletedAtUtc = evidence.SucceededAtUtc, TelegramMessageId = evidence.TelegramMessageId
            })
        } };
        RevisionImage exported = MonitoringSnapshotRows.Export(image);
        using var context = RevisionFixture.CreateContext();
        SharedSnapshotRow observation = exported.ProjectionRows.First(row => row.Entity == typeof(ObserverResult).FullName);

        // Act
        SnapshotImageValidation.Validate(context, exported);
        Exception? missing = Record.Exception(() => SnapshotImageValidation.Validate(context, exported with
        {
            ProjectionRows = exported.ProjectionRows.Remove(observation)
        }));
        Exception? fakeProof = Record.Exception(() => SnapshotImageValidation.Validate(context, exported with
        {
            Monitoring = exported.Monitoring with { ConfirmedDeliveries = exported.Monitoring.ConfirmedDeliveries.Clear() }
        }));

        // Assert
        Assert.NotNull(missing);
        Assert.IsType<InvalidDataException>(fakeProof);
        Assert.True(MonitoringRecipients.HasConfirmedProblem(exported, incidentId, chatId));
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    private static async Task<RevisionImage> Outage(MonitoringHarness source)
    {
        AcceptedValue cell = source.State.Current!.Values.Values.Single(item => item.Kind == DeletedEntityKind.Server);
        MonitoringTarget target = MonitoringTargets.Find(source.State.Current, cell.Id)!;
        for (int number = 1; number <= 3; number++)
        {
            MonitoringObservation observation = new(Guid.NewGuid(), cell.Id, source.Options.NodeId, source.Options.StorageGeneration,
                target.CheckGeneration, target.ConfigRevision, number, source.Time.Now.AddSeconds(number - 4), 1, false, "Timeout", null, null, true);
            await source.Dispatcher.AcceptAsync(MonitoringCommands.Record(source.State.Current!, source.Options.NodeId,
                source.Options.StorageGeneration, [observation]), source.Options.NodeId, TestContext.Current.CancellationToken);
        }

        await source.Dispatcher.AcceptAsync(MonitoringCommands.Evaluate(source.State.Current!, target, source.Options.NodeId,
            source.Options.StorageGeneration, 1, source.Time.Now.AddSeconds(-1), true, source.Time.Now), source.Options.NodeId,
            TestContext.Current.CancellationToken);
        return source.State.Current!;
    }

    private static (RevisionStateStore State, RevisionClusterRecovery Recovery, ClusterStateMachine Machine) Create(
        RecoveryAdmission proof, MonitoringHarness source, RevisionSnapshotStore snapshots, MonitoringHarness.Projection projection)
    {
        RevisionStateStore state = new();
        RevisionClusterRecovery recovery = new(new Admission(proof), state, new(state, new(), projection), projection,
            snapshots, source.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(source.Options));
        return (state, recovery, new(recovery, new NodeReadiness(source.Time)));
    }

    private static WriteAheadLog.Options WalOptions(string path)
    {
        return new() { Location = path, FlushInterval = TimeSpan.Zero, FlushOnCommit = true, HashAlgorithm = WriteAheadLog.IntegrityHashAlgorithm.Crc64 };
    }

    private class Admission(RecoveryAdmission proof) : IRecoveryAdmission
    {
        public ValueTask<RecoveryAdmission?> ReadAsync(CancellationToken token)
        {
            return ValueTask.FromResult<RecoveryAdmission?>(proof);
        }
    }
}
