using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Web.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class ManagedSupervisorTests
{
    [Fact]
    public async Task QuarantineAndReadyUseOneGenerationWithoutStartingSameLoopTwice()
    {
        // Arrange
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        InstallationState state = new();
        LocalInstallationProfile profile = Profile();
        state.Publish(profile);
        RuntimeWriteGate gate = new();
        using Catalog catalog = new();
        using ManagedWorkerSupervisor supervisor = Supervisor(services, state, gate, catalog, new Admission());

        // Act
        await supervisor.StartQuarantinedAsync(profile, CancellationToken.None);
        await catalog.Initial.Started.Task;
        LocalInstallationProfile ready = profile with { Phase = InstallationPhase.ClusterReady, ProfileRevision = 2 };
        state.Publish(ready);
        await supervisor.PublishReadyAsync(ready, CancellationToken.None);
        await catalog.Additional.Started.Task;
        bool drained = await supervisor.DrainAsync(1, TimeSpan.FromSeconds(1), CancellationToken.None);

        // Assert
        Assert.True(drained);
        Assert.Equal(1, catalog.Initial.Starts);
        Assert.Equal(1, catalog.Additional.Starts);
        Assert.True(catalog.Initial.Completion.IsCompleted);
        Assert.True(catalog.Additional.Completion.IsCompleted);
        Assert.False(state.CommonOpen);
    }

    [Fact]
    public async Task FailedDrainKeepsWriteGateClosedUntilAllTasksActuallyFinish()
    {
        // Arrange
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        InstallationState state = new();
        LocalInstallationProfile profile = Profile();
        state.Publish(profile);
        RuntimeWriteGate gate = new();
        using Catalog catalog = new();
        catalog.Initial.IgnoreCancellation = true;
        using ManagedWorkerSupervisor supervisor = Supervisor(services, state, gate, catalog, new Admission());
        await supervisor.StartQuarantinedAsync(profile, CancellationToken.None);
        await catalog.Initial.Started.Task;

        // Act
        bool failed = await supervisor.DrainAsync(1, TimeSpan.FromMilliseconds(25), CancellationToken.None);
        Exception? reopened = Record.Exception(() => gate.Open(2));
        catalog.Initial.Release.TrySetResult();
        bool completed = await supervisor.DrainAsync(1, TimeSpan.FromSeconds(1), CancellationToken.None);

        // Assert
        Assert.False(failed);
        Assert.IsType<InvalidOperationException>(reopened);
        Assert.True(completed);
        Assert.False(state.CommonOpen);
    }

    [Fact]
    public async Task DefaultAdmissionRefusesBeforeResolvingAnyModeSpecificWorker()
    {
        // Arrange
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        InstallationState state = new();
        LocalInstallationProfile profile = Profile();
        state.Publish(profile);
        using Catalog catalog = new();
        using ManagedWorkerSupervisor supervisor = Supervisor(services, state, new(), catalog, new UnavailableManagedRuntimeAdmission());

        // Act
        Exception? denied = await Record.ExceptionAsync(() => supervisor.StartQuarantinedAsync(profile, CancellationToken.None));

        // Assert
        Assert.Equal("OnlineRecoveryAuthorityNotInstalled", denied!.Message);
        Assert.Equal(0, catalog.Resolves);
        Assert.Equal(0, catalog.Initial.Starts);
    }

    private static LocalInstallationProfile Profile()
    {
        return new(Guid.NewGuid(), "Join", InstallationPhase.ClusterQuarantine, 1, 1, 1, Guid.NewGuid(), DateTimeOffset.UtcNow);
    }

    private static ManagedWorkerSupervisor Supervisor(IServiceProvider services, InstallationState state,
        RuntimeWriteGate gate, Catalog catalog, IManagedRuntimeAdmission admission)
    {
        return new(services, Options.Create(new InstallationOptions { InstallationIntent = "Setup" }),
            Options.Create(new ClusterOptions()), state, gate, admission, TimeProvider.System, catalog,
            NullLogger<ManagedWorkerSupervisor>.Instance);
    }

    private class Admission : IManagedRuntimeAdmission
    {
        public Task<bool> CanStartAsync(LocalInstallationProfile profile, CancellationToken token)
        {
            return Task.FromResult(true);
        }

        public Task<bool> CanWorkAsync(LocalInstallationProfile profile, CancellationToken token)
        {
            return Task.FromResult(true);
        }
    }

    private class Catalog : IManagedWorkerCatalog, IDisposable
    {
        public Worker Initial { get; } = new();
        public Worker Additional { get; } = new();
        public int Resolves { get; private set; }
        public IReadOnlyList<RestartableWorker> Resolve(bool cluster, bool quarantine, bool managedSetup)
        {
            Assert.True(cluster);
            Assert.True(managedSetup);
            Resolves++;
            if (quarantine)
            {
                return [Initial];
            }

            return [Initial, Additional];
        }

        public void Dispose()
        {
            Initial.Release.TrySetResult();
            Initial.Dispose();
            Additional.Dispose();
        }
    }

    private class Worker : RestartableWorker
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IgnoreCancellation { get; set; }
        public int Starts { get; private set; }
        protected override async Task ExecuteAsync(CancellationToken token)
        {
            Starts++;
            Started.TrySetResult();
            if (IgnoreCancellation)
            {
                await Release.Task;
            }
            else
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        }
    }
}
