using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Monitoring;
using UptimeHarbor.Infrastructure.Telegram;
using Microsoft.Extensions.Logging;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Единственный host owner всех loops. Setup не разрешает создание native WAL.</summary>
public class ManagedWorkerSupervisor(IServiceProvider services, IOptions<InstallationOptions> installation,
    IOptions<ClusterOptions> configured, InstallationState state, RuntimeWriteGate gate,
    IManagedRuntimeAdmission admission, TimeProvider time, IManagedWorkerCatalog catalog,
    ILogger<ManagedWorkerSupervisor> logger)
    : BackgroundService, IInstallationRuntime
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly List<RestartableWorker> running = [];
    private long generation;
    private long? renewedAt;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (installation.Value.Setup)
        {
            LocalInstallationProfile? profile = state.Current;
            if (profile?.ManagedRestart == true && profile.Phase == InstallationPhase.ClusterQuarantine)
            {
                await StartQuarantinedAsync(profile, cancellationToken);
            }

            await base.StartAsync(cancellationToken);
            return;
        }

        await StartGroupAsync(configured.Value.Enabled, 1, cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (installation.Value.Setup && (!renewedAt.HasValue || time.GetElapsedTime(renewedAt.Value) >= TimeSpan.FromSeconds(30)))
            {
                await RenewProcessLeaseAsync(stoppingToken);
                renewedAt = time.GetTimestamp();
            }

            bool unexpected = false;
            long failedGeneration = 0;
            await lifecycle.WaitAsync(stoppingToken);
            try
            {
                if (running.Any(worker => worker.Completion.IsCompleted)
                    && state.Current?.Phase != InstallationPhase.FailedNeedsAction)
                {
                    _ = gate.CloseAsync();
                    unexpected = true;
                    failedGeneration = generation;
                }
            }
            finally
            {
                lifecycle.Release();
            }

            if (unexpected)
            {
                if (!installation.Value.Setup)
                {
                    throw new InvalidOperationException("ManagedWorkerStoppedUnexpectedly");
                }

                await RecordWorkerFailureAsync(failedGeneration, stoppingToken);
            }
        }
    }

    private async Task RecordWorkerFailureAsync(long failedGeneration, CancellationToken token)
    {
        await state.Transition.WaitAsync(token);
        try
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            ILocalSetupUnitOfWork work = scope.ServiceProvider.GetRequiredService<ILocalSetupUnitOfWork>();
            LocalInstallationProfile profile = await work.Profiles.ReadAsync(token)
                ?? throw new InvalidDataException("InstallationProfileMissing");
            if (profile.RuntimeGeneration != failedGeneration)
            {
                return;
            }

            LocalInstallationProfile failed = profile with
            {
                Phase = InstallationPhase.FailedNeedsAction, ErrorCode = "RuntimeWorkerFailed",
                ProfileRevision = profile.ProfileRevision + 1
            };
            await work.ExecuteInTransactionAsync(async cancellation =>
                await work.Profiles.ReplaceAsync(failed, profile.ProfileRevision, cancellation), token);
            state.Publish(failed);
            logger.LogWarning("{EventName} {AttemptId} {Phase} {Revision} {Generation} {Reason}",
                "RuntimeTransitionFailed", failed.AttemptId, failed.Phase, failed.ProfileRevision,
                failed.RuntimeGeneration, failed.ErrorCode);
            await DrainAsync(failedGeneration, TimeSpan.FromSeconds(10), token);
        }
        finally
        {
            state.Transition.Release();
        }
    }

    private async Task RenewProcessLeaseAsync(CancellationToken token)
    {
        await state.Transition.WaitAsync(token);
        try
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            ILocalSetupUnitOfWork work = scope.ServiceProvider.GetRequiredService<ILocalSetupUnitOfWork>();
            InstallationLease lease = services.GetRequiredService<InstallationLease>();
            LocalInstallationProfile profile = await work.Profiles.ReadAsync(token)
                ?? throw new InvalidOperationException("InstallationProfileMissing");
            if (profile.BootId != lease.BootId || profile.ProfileRevision != state.Current?.ProfileRevision)
            {
                throw new InvalidOperationException("InstallationLeaseLost");
            }

            LocalInstallationProfile renewed = profile with
            {
                ProfileRevision = profile.ProfileRevision + 1, LeaseUntilUtc = time.GetUtcNow().AddMinutes(2)
            };
            await work.ExecuteInTransactionAsync(async cancellation =>
                await work.Profiles.ReplaceAsync(renewed, profile.ProfileRevision, cancellation), token);
            state.Publish(renewed);
        }
        finally
        {
            state.Transition.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (!await DrainAsync(generation, TimeSpan.FromSeconds(10), cancellationToken))
        {
            throw new InvalidOperationException("RuntimeDrainFailed");
        }
    }

    public async Task<bool> DrainAsync(long expectedGeneration, TimeSpan budget, CancellationToken token)
    {
        await lifecycle.WaitAsync(token);
        try
        {
            if (generation != 0 && generation != expectedGeneration)
            {
                return false;
            }

            Task writes = gate.CloseAsync();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(budget);
            Task[] stops = running.Select(worker => worker.StopAsync(timeout.Token)).ToArray();
            try
            {
                await Task.WhenAll(stops.Append(writes)).WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                return false;
            }

            running.Clear();
            return true;
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task StartQuarantinedAsync(LocalInstallationProfile profile, CancellationToken token)
    {
        if (profile.Phase != InstallationPhase.ClusterQuarantine || profile != state.Current)
        {
            throw new InvalidOperationException("StaleRuntimeProfile");
        }

        if (!await admission.CanStartAsync(profile, token))
        {
            throw new InvalidOperationException("OnlineRecoveryAuthorityNotInstalled");
        }

        await StartGroupAsync(true, profile.RuntimeGeneration, token, quarantine: true);
    }

    public async Task PublishReadyAsync(LocalInstallationProfile profile, CancellationToken token)
    {
        if (profile != state.Current || profile.Phase != InstallationPhase.ClusterReady)
        {
            throw new InvalidOperationException("StaleRuntimeProfile");
        }

        if (!await admission.CanWorkAsync(profile, token))
        {
            throw new InvalidOperationException("OnlineActivationRequired");
        }

        await StartGroupAsync(true, profile.RuntimeGeneration, token);
    }

    private async Task StartGroupAsync(bool cluster, long nextGeneration, CancellationToken token, bool quarantine = false)
    {
        await lifecycle.WaitAsync(token);
        try
        {
            if (running.Count != 0 && (generation != nextGeneration || !cluster || quarantine))
            {
                throw new InvalidOperationException("RuntimeAlreadyStarted");
            }

            if (running.Count == 0)
            {
                gate.Open(nextGeneration);
                generation = nextGeneration;
            }

            IReadOnlyList<RestartableWorker> requested = catalog.Resolve(cluster, quarantine, installation.Value.Setup);
            foreach (RestartableWorker worker in requested.Where(item => !running.Contains(item)))
            {
                worker.ConfigureGeneration(gate, nextGeneration);
                await worker.StartAsync(token);
                running.Add(worker);
            }
        }
        catch
        {
            _ = gate.CloseAsync();
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(10));
            try
            {
                await Task.WhenAll(running.Select(worker => worker.StopAsync(cleanup.Token))).WaitAsync(cleanup.Token);
                running.Clear();
            }
            catch (Exception)
            {
                // Не освобождаем владение незавершёнными loops; новый Start останется запрещён.
            }

            throw;
        }
        finally
        {
            lifecycle.Release();
        }
    }
}
