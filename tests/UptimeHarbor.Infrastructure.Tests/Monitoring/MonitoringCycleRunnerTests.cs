using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Infrastructure.Monitoring;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Monitoring;

public class MonitoringCycleRunnerTests
{
    [Fact]
    public async Task DifferentObjects_AreCheckedInParallel()
    {
        FakeMonitoringService service = new(
        [
            ServerTarget("https://server-1.example/health"),
            ServerTarget("https://server-2.example/health")
        ]);
        CoordinatedServerChecker checker = new(expectedConcurrentCalls: 2);
        using ServiceProvider provider = CreateProvider(service, checker, new SuccessfulContainerChecker());
        MonitoringCycleRunner runner = CreateRunner(provider);

        Task cycle = runner.RunCycleAsync(TestContext.Current.CancellationToken);
        await checker.ExpectedCallsStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, checker.MaximumConcurrentCalls);
        checker.Release.TrySetResult();
        await cycle;
    }

    [Fact]
    public async Task SameObject_IsNotCheckedConcurrentlyAcrossCycles()
    {
        MonitoringTargetDto target = ServerTarget("https://server.example/health");
        FakeMonitoringService service = new([target]);
        CoordinatedServerChecker checker = new(expectedConcurrentCalls: 1);
        using ServiceProvider provider = CreateProvider(service, checker, new SuccessfulContainerChecker());
        MonitoringCycleRunner runner = CreateRunner(provider);

        Task firstCycle = runner.RunCycleAsync(TestContext.Current.CancellationToken);
        await checker.ExpectedCallsStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Task secondCycle = runner.RunCycleAsync(TestContext.Current.CancellationToken);
        await secondCycle;

        Assert.Equal(1, checker.CallCount);
        checker.Release.TrySetResult();
        await firstCycle;
    }

    [Fact]
    public async Task ObjectFailure_DoesNotStopOtherChecksOrRetryImmediately()
    {
        FakeMonitoringService service = new(
        [
            ServerTarget("https://failing.example/health"),
            ServerTarget("https://successful.example/health")
        ]);
        SelectiveServerChecker checker = new();
        using ServiceProvider provider = CreateProvider(service, checker, new SuccessfulContainerChecker());
        MonitoringCycleRunner runner = CreateRunner(provider);

        await runner.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, checker.CallCount);
        Assert.Single(service.RecordedServerIds);
    }

    [Fact]
    public async Task ServerAndItsContainer_AreCheckedIndependently()
    {
        Guid serverId = Guid.NewGuid();
        FakeMonitoringService service = new(
        [
            new MonitoringTargetDto(
                serverId,
                MonitoringObjectKind.Server,
                "https://server.example/health"),
            new MonitoringTargetDto(
                Guid.NewGuid(),
                MonitoringObjectKind.Container,
                "https://container.example/health")
        ]);
        CountingServerChecker serverChecker = new();
        SuccessfulContainerChecker containerChecker = new();
        using ServiceProvider provider = CreateProvider(service, serverChecker, containerChecker);
        MonitoringCycleRunner runner = CreateRunner(provider);

        await runner.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, serverChecker.CallCount);
        Assert.Equal(1, containerChecker.CallCount);
        Assert.Single(service.RecordedServerIds);
        Assert.Single(service.RecordedContainerIds);
    }

    [Fact]
    public async Task Worker_WhenStopped_CancelsActiveCycleAndCompletes()
    {
        BlockingCycleRunner cycleRunner = new();
        MonitoringWorker worker = new(
            cycleRunner,
            Options.Create(new MonitoringOptions
            {
                IntervalSeconds = 30,
                TimeoutSeconds = 30,
                FailuresBeforeUnavailable = 3
            }),
            TimeProvider.System);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await cycleRunner.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(cycleRunner.CancellationObserved);
    }

    private static ServiceProvider CreateProvider(
        IMonitoringService monitoringService,
        IServerEndpointChecker serverChecker,
        IContainerEndpointChecker containerChecker)
    {
        ServiceCollection services = new();
        services.AddSingleton(monitoringService);
        services.AddSingleton(serverChecker);
        services.AddSingleton(containerChecker);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static MonitoringCycleRunner CreateRunner(ServiceProvider provider)
    {
        return new MonitoringCycleRunner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MonitoringOptions
            {
                IntervalSeconds = 30,
                TimeoutSeconds = 30,
                FailuresBeforeUnavailable = 3
            }),
            NullLogger<MonitoringCycleRunner>.Instance);
    }

    private static MonitoringTargetDto ServerTarget(string endpointUrl)
    {
        return new MonitoringTargetDto(Guid.NewGuid(), MonitoringObjectKind.Server, endpointUrl);
    }

    private class FakeMonitoringService(IReadOnlyList<MonitoringTargetDto> targets)
        : IMonitoringService
    {
        public List<Guid> RecordedServerIds { get; } = [];

        public List<Guid> RecordedContainerIds { get; } = [];

        public Task<IReadOnlyList<MonitoringTargetDto>> GetEnabledTargetsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(targets);
        }

        public Task<bool> RecordServerResultAsync(
            Guid serverId,
            string checkedEndpointUrl,
            ServerEndpointCheckResult result,
            int failuresBeforeUnavailable,
            CancellationToken cancellationToken = default)
        {
            lock (RecordedServerIds)
            {
                RecordedServerIds.Add(serverId);
            }

            return Task.FromResult(true);
        }

        public Task<bool> RecordContainerResultAsync(
            Guid containerId,
            string checkedEndpointUrl,
            ContainerEndpointCheckResult result,
            int failuresBeforeUnavailable,
            CancellationToken cancellationToken = default)
        {
            lock (RecordedContainerIds)
            {
                RecordedContainerIds.Add(containerId);
            }

            return Task.FromResult(true);
        }
    }

    private class CoordinatedServerChecker(int expectedConcurrentCalls)
        : IServerEndpointChecker
    {
        private int activeCalls;

        public int CallCount { get; private set; }

        public int MaximumConcurrentCalls { get; private set; }

        public TaskCompletionSource ExpectedCallsStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ServerEndpointCheckResult> CheckAsync(
            string endpointUrl,
            CancellationToken cancellationToken = default)
        {
            int currentCalls = Interlocked.Increment(ref activeCalls);
            CallCount++;
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, currentCalls);
            if (CallCount == expectedConcurrentCalls)
            {
                ExpectedCallsStarted.TrySetResult();
            }

            await Release.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref activeCalls);
            return ServerEndpointCheckResult.Succeeded(
                "server",
                new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero));
        }
    }

    private class SelectiveServerChecker : IServerEndpointChecker
    {
        public int CallCount { get; private set; }

        public Task<ServerEndpointCheckResult> CheckAsync(
            string endpointUrl,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (endpointUrl.Contains("failing", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unexpected object failure.");
            }

            return Task.FromResult(ServerEndpointCheckResult.Succeeded(
                "server",
                new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero)));
        }
    }

    private class CountingServerChecker : IServerEndpointChecker
    {
        public int CallCount { get; private set; }

        public Task<ServerEndpointCheckResult> CheckAsync(
            string endpointUrl,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(ServerEndpointCheckResult.Succeeded(
                "server",
                new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero)));
        }
    }

    private class SuccessfulContainerChecker : IContainerEndpointChecker
    {
        public int CallCount { get; private set; }

        public Task<ContainerEndpointCheckResult> CheckAsync(
            string endpointUrl,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            DateTimeOffset checkedAtUtc =
                new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
            return Task.FromResult(ContainerEndpointCheckResult.Succeeded(
                "container",
                checkedAtUtc.AddHours(-1),
                checkedAtUtc));
        }
    }

    private class BlockingCycleRunner : IMonitoringCycleRunner
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public async Task RunCycleAsync(CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }
}
