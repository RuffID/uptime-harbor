using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Installation;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class RuntimeGenerationTests
{
    [Fact]
    public async Task DrainWaitsForOldLoopDoubleStartFailsAndOldCallbackCannotPublishAfterRestart()
    {
        // Arrange
        RuntimeWriteGate gate = new();
        gate.Open(1);
        using ControlledWorker worker = new();
        worker.ConfigureGeneration(gate, 1);
        await worker.StartAsync(CancellationToken.None);
        await worker.Started.Task;
        ExecutionContext callback = worker.Captured!;

        // Act
        Exception? duplicate = await Record.ExceptionAsync(() => worker.StartAsync(CancellationToken.None));
        Task draining = gate.CloseAsync();
        bool waiting = !draining.IsCompleted;
        await worker.StopAsync(CancellationToken.None);
        await draining;
        gate.Open(2);
        Exception? stale = Record.Exception(() => ExecutionContext.Run(callback, _ => RuntimeGenerationContext.Verify(), null));
        using UptimeHarborDbContext context = new(new DbContextOptionsBuilder<UptimeHarborDbContext>().UseSqlServer().Options);
        Exception? staleWrite = Record.Exception(() => ExecutionContext.Run(callback, _ => context.SaveChanges(), null));

        // Assert
        Assert.True(waiting);
        Assert.IsType<InvalidOperationException>(duplicate);
        Assert.IsType<InvalidOperationException>(stale);
        Assert.Equal("StaleRuntimeGeneration", staleWrite!.Message);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.True(worker.Completion.IsCompleted);

        // Act
        worker.ConfigureGeneration(gate, 2);
        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);
        await gate.CloseAsync();

        // Assert
        Assert.True(worker.Completion.IsCompleted);
    }

    [Fact]
    public void LocalReadyProfileDoesNotOpenCommonAccessAndStaleRevisionCannotReplaceIt()
    {
        // Arrange
        InstallationState state = new();
        LocalInstallationProfile profile = new(Guid.NewGuid(), "Bootstrap", InstallationPhase.ClusterReady,
            2, 2, 2, Guid.NewGuid(), DateTimeOffset.UtcNow);

        // Act
        state.Publish(profile);
        Exception? stale = Record.Exception(() => state.Publish(profile with { ProfileRevision = 1 }));

        // Assert
        Assert.False(state.CommonOpen);
        Assert.IsType<InvalidOperationException>(stale);
        Assert.Equal(profile, state.Current);
    }

    private class ControlledWorker : RestartableWorker
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExecutionContext? Captured { get; private set; }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Captured = ExecutionContext.Capture();
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
    }
}
