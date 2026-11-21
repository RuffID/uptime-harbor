using Microsoft.Extensions.Options;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Monitoring;
using UptimeHarbor.Infrastructure.Telegram;

namespace UptimeHarbor.Web.Cluster;

public class RuntimeWorkerSupervisor(IServiceProvider services, IOptions<ClusterOptions> options,
    RuntimeWriteGate gate, TimeProvider time) : BackgroundService
{
    private const long GENERATION = 1;
    private static readonly TimeSpan STOP_TIMEOUT = TimeSpan.FromSeconds(10);
    private readonly List<RestartableWorker> running = [];

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<RestartableWorker> workers;
        if (options.Value.Enabled)
        {
            workers =
            [
                services.GetRequiredService<ClusterHealthWorker>(), services.GetRequiredService<ClusterPeerRuntime>(),
                services.GetRequiredService<ClusterLifecycleWorker>(), services.GetRequiredService<ClusterRevisionWorker>(),
                services.GetRequiredService<ClusterSnapshotWorker>(), services.GetRequiredService<ClusterMonitoringWorker>(),
                services.GetRequiredService<ClusterDeliveryWorker>()
            ];
        }
        else
        {
            workers = [services.GetRequiredService<MonitoringWorker>(), services.GetRequiredService<TelegramOutboxWorker>()];
        }

        gate.Open(GENERATION);
        try
        {
            foreach (RestartableWorker worker in workers)
            {
                worker.ConfigureGeneration(gate, GENERATION);
                await worker.StartAsync(cancellationToken);
                running.Add(worker);
            }

            await base.StartAsync(cancellationToken);
        }
        catch
        {
            await DrainAsync(CancellationToken.None);
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (running.FirstOrDefault(worker => worker.Completion.IsCompleted) is { } stopped)
            {
                _ = gate.CloseAsync();
                await stopped.Completion;
                throw new InvalidOperationException($"Runtime worker {stopped.GetType().Name} stopped unexpectedly.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await DrainAsync(cancellationToken);
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        Task writes = gate.CloseAsync();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(STOP_TIMEOUT);
        await Task.WhenAll(running.Select(worker => worker.StopAsync(timeout.Token)).Append(writes))
            .WaitAsync(timeout.Token);
        running.Clear();
    }
}
