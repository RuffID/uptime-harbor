using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

/// <summary>Штатный loop расписания в managed Bootstrap Direct.</summary>
public sealed class VoterCredentialRotationWorker(IServiceScopeFactory scopes, InstallationState installation,
    RuntimeWriteGate writes, IOptions<ClusterOptions> options, TimeProvider time,
    ILogger<VoterCredentialRotationWorker> logger) : RestartableWorker
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (installation.CommonOpen && installation.Current is { Phase: InstallationPhase.ClusterReady, Mode: "Bootstrap" } profile
                    && options.Value.TlsBoundary == "Direct")
                {
                    using RuntimeGenerationContext guard = RuntimeGenerationContext.Extend(() =>
                    {
                        writes.Verify(profile.RuntimeGeneration);
                        if (installation.Current?.RuntimeGeneration != profile.RuntimeGeneration)
                        {
                            throw new InvalidOperationException("StaleRuntimeGeneration");
                        }
                    });
                    RuntimeGenerationContext.Verify();
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<LocalVoterRenewal>().RunScheduledAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (CanRetry(error))
            {
                logger.LogWarning("VoterCredentialRotationDeferred {NodeId}", options.Value.NodeId);
            }

            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
        }
    }

    private static bool CanRetry(Exception error)
    {
        return error is IOException or HttpRequestException or TimeoutException or UnauthorizedAccessException or OperationCanceledException
            || error is InvalidOperationException
            {
                Message: "EnrollmentReadMajorityUnavailable" or "EnrollmentReadMajorityChanged"
                    or "EnrollmentPeerConsensusUnavailable" or "EnrollmentPeerConsensusChanged"
                    or "EnrollmentPeerProjectionUnavailable" or "EnrollmentLeaderUnavailable" or "DecisionUnavailable"
                    or "Cluster writer is not ready." or "Raft recovery is unconfirmed." or "StaleRuntimeGeneration"
            };
    }
}
