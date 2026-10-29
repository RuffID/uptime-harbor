using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Monitoring;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class MonitoringRuntimeTests
{
    [Fact]
    public async Task IndependentTargetsRunConcurrentlyWithSeparateCaptures()
    {
        // Arrange
        using MonitoringHarness runtime = new();
        RevisionImage image = runtime.State.Current!;
        AcceptedValue first = image.Values.Values.Single(item => item.Kind == DeletedEntityKind.Server);
        AcceptedValue second = first with { Id = Guid.NewGuid(), Value = first.Value with { Name = "second", Generation = Guid.NewGuid() } };
        AcceptedValue unassigned = first with { Id = Guid.NewGuid(), Value = first.Value with { Name = "unassigned", ObserverNodes = [] } };
        runtime.State.Publish(image with { Values = image.Values.Add(second.Id, second).Add(unassigned.Id, unassigned) });
        using ParallelEndpoint endpoint = new();
        ClusterMonitoringCycle cycle = new(runtime.State, runtime, runtime.Readiness, Options.Create(runtime.Options),
            runtime.Services.GetRequiredService<IServiceScopeFactory>(), endpoint,
            new(runtime.Readiness, Options.Create(new MonitoringOptions())), runtime.Time, NullLogger<ClusterMonitoringCycle>.Instance);

        // Act
        Task running = cycle.RunOnceAsync(TestContext.Current.CancellationToken);
        try
        {
            await endpoint.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally
        {
            endpoint.Release.TrySetResult();
            await running;
        }

        // Assert
        Assert.Equal(2, endpoint.Calls);
        Assert.Equal(2, runtime.Storage.Saved.Count);
        Assert.Equal(2, runtime.Storage.Saved.Select(item => item.StreamId).Distinct().Count());
    }

    [Fact]
    public async Task CapturedBatchLostResponseAndRestartUsesAcceptedSequenceAndDoesNotDuplicate()
    {
        // Arrange
        using MonitoringHarness runtime = new();
        var (_, fact) = ObservationCaptureTests.Scenario();
        AcceptedValue target = runtime.State.Current!.Values.Values.Single(cell => cell.Kind == DeletedEntityKind.Server);
        fact = fact with { TargetId = target.Id, ObserverNodeId = runtime.Options.NodeId,
            StorageGeneration = runtime.Options.StorageGeneration, CheckGeneration = target.Value.Generation, ConfigRevision = target.Revision };
        ObservationCaptureService capture = runtime.Services.GetRequiredService<ObservationCaptureService>();
        for (int number = 0; number < 4; number++)
        {
            await capture.CaptureAsync(fact with { Id = Guid.NewGuid(), CheckedAtUtc = runtime.Time.Now.AddSeconds(-4 + number) }, TestContext.Current.CancellationToken);
        }

        ObservationReplicationPump pump = new(runtime.Services.GetRequiredService<IServiceScopeFactory>(), runtime.State, runtime, Options.Create(runtime.Options));
        runtime.LoseTransportResponse = true;

        // Act
        Exception? lost = await Record.ExceptionAsync(() => pump.PumpAsync(TestContext.Current.CancellationToken));
        runtime.LoseTransportResponse = false;
        ObservationReplicationPump restarted = new(runtime.Services.GetRequiredService<IServiceScopeFactory>(), runtime.State, runtime, Options.Create(runtime.Options));
        await restarted.PumpAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<IOException>(lost);
        Assert.Equal(4, runtime.State.Current!.Monitoring.Observations.Count);
        Assert.Equal(4, Assert.Single(runtime.State.Current.Monitoring.Streams.Values).Sequence);
        Assert.Equal(4, runtime.Storage.Saved.Count);
        Assert.Single(runtime.Store.Writes);
        Assert.Empty(runtime.Audit);
    }

    [Fact]
    public async Task EndpointCycleChecksAssignedTargetsAndStopsUntilDatabaseRecoveryIsConfirmed()
    {
        // Arrange
        using MonitoringHarness runtime = new();
        Endpoint endpoint = new();
        IServiceScopeFactory scopes = runtime.Services.GetRequiredService<IServiceScopeFactory>();
        ClusterMonitoringCycle cycle = new(runtime.State, runtime, runtime.Readiness, Options.Create(runtime.Options), scopes,
            endpoint, new MonitoringProgressWatchdog(runtime.Readiness, Options.Create(new MonitoringOptions())), runtime.Time,
            NullLogger<ClusterMonitoringCycle>.Instance);

        // Act
        await cycle.RunOnceAsync(TestContext.Current.CancellationToken);
        await cycle.RunOnceAsync(TestContext.Current.CancellationToken);
        runtime.Time.Now = runtime.Time.Now.AddMinutes(1);
        runtime.Readiness.Report("Database", false, "DatabaseUnavailable", TimeSpan.FromHours(1));
        await cycle.RunOnceAsync(TestContext.Current.CancellationToken);
        runtime.Readiness.Report("Database", true, "Ready", TimeSpan.FromHours(1));
        await cycle.RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, endpoint.Calls);
        Assert.Single(runtime.Storage.Saved);
        Assert.False(runtime.Readiness.CanWork(runtime));

        // Act
        Assert.True(runtime.Readiness.ConfirmRecovery(runtime.Readiness.RecoveryVersion, runtime.Accepted, runtime.Consensus!));
        await cycle.RunOnceAsync(TestContext.Current.CancellationToken);
        runtime.Options.Mode = "Disabled";
        runtime.Time.Now = runtime.Time.Now.AddMinutes(1);
        await cycle.RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, endpoint.Calls);
        Assert.Equal([1L, 2L], runtime.Storage.Saved.Select(item => item.Sequence));
    }

    [Theory]
    [InlineData(null, "PeerUnavailable")]
    [InlineData("Database", "DatabaseUnavailable")]
    [InlineData("Monitoring", "WorkerStalled")]
    [InlineData("Telegram", "TelegramUnavailable")]
    public async Task PeerCapturePreservesActualNodeAndReportedCauseWithoutGuessingDatabase(string? failed, string expected)
    {
        // Arrange
        using MonitoringHarness runtime = new();
        Guid peer = Guid.NewGuid();
        RevisionImage image = runtime.State.Current!;
        runtime.State.Publish(image with { Monitoring = image.Monitoring with { Nodes = image.Monitoring.Nodes.Add(peer, Guid.NewGuid()) } });
        PeerHealthSnapshot? health = null;
        if (failed is not null)
        {
            Dictionary<string, HealthResult> components = new();
            foreach (string name in new[] { "Database", "Projection", "Monitoring", "Telegram" })
            {
                string status = "Ready";
                if (name == failed)
                {
                    status = "Unavailable";
                }

                components.Add(name, new(status, "Unknown", TimeSpan.Zero));
            }

            health = new(peer, Guid.NewGuid(), 1, null, 1, 3, 3, false, 0, components);
        }

        PeerObservationCapture capture = new(runtime.State, runtime, runtime.Readiness, Options.Create(runtime.Options),
            runtime.Services.GetRequiredService<IServiceScopeFactory>(), runtime.Time);

        // Act
        await capture.CaptureAsync(peer, health, 3, TestContext.Current.CancellationToken);
        MonitoringObservation observation = JsonSerializer.Deserialize<MonitoringObservation>(Assert.Single(runtime.Storage.Saved).Payload)!;
        RevisionCommand command = MonitoringCommands.Record(runtime.State.Current!, runtime.Options.NodeId, runtime.Options.StorageGeneration, [observation]);
        await runtime.Dispatcher.AcceptAsync(command, runtime.Options.NodeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(peer, observation.TargetId);
        Assert.Equal(runtime.Options.NodeId, observation.ObserverNodeId);
        Assert.Equal(expected, observation.FailureKind);
        Assert.Equal(DeletedEntityKind.Node, command.EntityKind);
        Assert.True(observation.ClockConfirmed);
    }

    [Fact]
    public async Task AcceptedTransitionsAuditOnlyAfterCommitAndReplayCannotRepeatSuccess()
    {
        // Arrange
        using MonitoringHarness runtime = new();
        AcceptedValue cell = runtime.State.Current!.Values.Values.Single(item => item.Kind == DeletedEntityKind.Server);
        MonitoringTarget target = MonitoringTargets.Find(runtime.State.Current, cell.Id)!;
        for (int number = 1; number <= 3; number++)
        {
            MonitoringObservation observation = new(Guid.NewGuid(), cell.Id, runtime.Options.NodeId, runtime.Options.StorageGeneration,
                target.CheckGeneration, target.ConfigRevision, number, runtime.Time.Now.AddSeconds(number - 4), 1, false, "Timeout", null, null, true);
            await runtime.Dispatcher.AcceptAsync(MonitoringCommands.Record(runtime.State.Current!, runtime.Options.NodeId,
                runtime.Options.StorageGeneration, [observation]), runtime.Options.NodeId, TestContext.Current.CancellationToken);
        }

        RevisionCommand evaluate = MonitoringCommands.Evaluate(runtime.State.Current!, target, runtime.Options.NodeId,
            runtime.Options.StorageGeneration, 1, runtime.Time.Now, false, runtime.Time.Now);
        runtime.Store.Fail = true;

        // Act
        Exception? failed = await Record.ExceptionAsync(() => runtime.Dispatcher.AcceptAsync(evaluate, runtime.Options.NodeId, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<IOException>(failed);
        Assert.Empty(runtime.Audit);
        Assert.False(runtime.State.Available);

        // Act
        runtime.Store.Fail = false;
        await runtime.Engine.ConfirmRecoveryAsync(_ => Task.FromResult(true), TestContext.Current.CancellationToken);
        await runtime.Dispatcher.AcceptAsync(evaluate, runtime.Options.NodeId, TestContext.Current.CancellationToken);
        await runtime.Dispatcher.AcceptAsync(evaluate, runtime.Options.NodeId, TestContext.Current.CancellationToken);
        await runtime.Engine.ApplyAsync(evaluate, runtime.State.Current!.Index + 1, 2, runtime.Options.NodeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(runtime.Audit);
        Assert.Equal(runtime.Options.NodeId, runtime.Audit[0].Node);
        Assert.Equal(Guid.Empty, evaluate.ActorUserId);

        // Act
        runtime.Consensus = runtime.Consensus! with { IsLeader = false };
        Exception? authority = await Record.ExceptionAsync(() => runtime.Dispatcher.AcceptAsync(evaluate with { OperationId = Guid.NewGuid() },
            runtime.Options.NodeId, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(authority);
        Assert.Single(runtime.Audit);
    }

    private class Endpoint : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }

        public HttpClient CreateClient(string name)
        {
            return new(this, false);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"name\":\"app\"}") });
        }
    }

    private class ParallelEndpoint : HttpMessageHandler, IHttpClientFactory
    {
        private int calls;
        public int Calls => calls;
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpClient CreateClient(string name)
        {
            return new(this, false);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                BothStarted.TrySetResult();
            }

            await Release.Task.WaitAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"name\":\"app\"}") };
        }
    }
}
