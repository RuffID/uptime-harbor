using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Восстанавливаемая доставка без legacy outbox. Ошибка одного чата изолирована.</summary>
public class AcceptedDeliveryDispatcher(RevisionStateStore accepted, IClusterStateView state, NodeReadiness readiness,
    SenderAuthorityGate gate, IRevisionTransport journal, IServiceScopeFactory scopes,
    IOptions<ClusterOptions> options, TimeProvider time, ILogger<AcceptedDeliveryDispatcher> logger,
    IOptions<UptimeHarbor.Infrastructure.Installation.InstallationOptions>? installation = null)
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private long recoveredVersion = -1;
    private long recoveredTerm = -1;
    private Guid afterResult;

    public bool ReconcileQueue()
    {
        if (installation?.Value.Setup == true)
        {
            return false;
        }

        if (!options.Value.Enabled || !accepted.Available || !readiness.CanSend(state))
        {
            return false;
        }

        long version = accepted.RecoveryVersion;
        long term = state.Consensus!.Term;
        if (recoveredVersion != version || recoveredTerm != term)
        {
            DeliveryQueue.Validate(accepted.Current!);
            if (!accepted.Available || accepted.RecoveryVersion != version || state.Consensus?.Term != term)
            {
                return false;
            }

            recoveredVersion = version;
            recoveredTerm = term;
        }

        return true;
    }

    public async Task RunOnceAsync(CancellationToken token)
    {
        if (installation?.Value.Setup == true)
        {
            return;
        }

        await serial.WaitAsync(token);
        try
        {
            if (!options.Value.Enabled || !accepted.Available || !state.Accepted.CaughtUp)
            {
                return;
            }

            await ForwardResultsAsync(token);
            if (!ReconcileQueue())
            {
                return;
            }

            await RecoverUnknownAsync(token);
            IReadOnlyList<MonitoringIntent> due = DeliveryQueue.Due(accepted.Current!, time.GetUtcNow(), 100);
            await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                async (intent, cancellation) => await DeliverAsync(intent.Id, cancellation));
        }
        finally
        {
            serial.Release();
        }
    }

    private async Task ForwardResultsAsync(CancellationToken token)
    {
        AcceptedDeliveryAttempt[] page = accepted.Current!.Monitoring.Attempts.Values
            .Where(attempt => attempt.Authority.LeaderNodeId == options.Value.NodeId)
            .OrderBy(item => item.Id.CompareTo(afterResult) <= 0).ThenBy(item => item.Id).Take(100).ToArray();
        foreach (AcceptedDeliveryAttempt attempt in page)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            IDeliveryResultUnitOfWork work = scope.ServiceProvider.GetRequiredService<IDeliveryResultUnitOfWork>();
            CapturedDeliveryResult? result = await work.Results.GetAsync(attempt.Id, token);
            if (result is not null)
            {
                await ForwardAsync(result, token);
            }

            afterResult = attempt.Id;
        }
    }

    private async Task ForwardAsync(CapturedDeliveryResult capture, CancellationToken token)
    {
        RevisionImage image = accepted.Current!;
        AcceptedDeliveryAttempt attempt = image.Monitoring.Attempts[capture.Id];
        if (attempt.Outcome == capture.Outcome && attempt.TelegramMessageId == capture.TelegramMessageId)
        {
            return;
        }

        Guid operation = MonitoringKeys.Id($"result/{capture.Id:N}/{capture.Outcome}/{capture.CompletedAtUtc:O}/{capture.TelegramMessageId}/{capture.FailureKind}");
        if (image.Results.ContainsKey(operation))
        {
            return;
        }

        DeliveryCommand payload = new()
        {
            AttemptId = capture.Id, Outcome = capture.Outcome, CompletedAtUtc = capture.CompletedAtUtc,
            TelegramMessageId = capture.TelegramMessageId, FailureKind = capture.FailureKind
        };
        RevisionCommand command = DeliveryCommands.Create(image, options.Value.NodeId, options.Value.StorageGeneration,
            "RecordDelivery", attempt.IntentId, operation, capture.CompletedAtUtc, payload, transport: true);
        IReadOnlyList<CommandResult> results = await journal.SendAsync([command], token);
        PendingReplicationPump.ValidateResults([command], results);
    }

    private async Task RecoverUnknownAsync(CancellationToken token)
    {
        RevisionImage image = accepted.Current!;
        AcceptedDeliveryAttempt[] unknown = image.Monitoring.Attempts.Values.Where(item => item.Outcome == AttemptOutcome.Unknown
            && time.GetUtcNow() - item.StartedAtUtc >= TimeSpan.FromSeconds(30)).OrderBy(item => item.Id).Take(100).ToArray();
        foreach (AcceptedDeliveryAttempt attempt in unknown)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            CapturedDeliveryResult? local = await scope.ServiceProvider.GetRequiredService<IDeliveryResultUnitOfWork>()
                .Results.GetAsync(attempt.Id, token);
            if (local is not null)
            {
                await ForwardAsync(local, token);
                continue;
            }

            ConsensusStatus status = state.Consensus!;
            SenderAuthorityKey key = new(status.ClusterId, status.Generation, status.MembershipVersion, status.Term,
                status.NodeId, state.Accepted.SenderEpoch);
            RevisionCommand command = DeliveryCommands.Create(accepted.Current!, options.Value.NodeId, options.Value.StorageGeneration,
                "AbandonDelivery", attempt.IntentId, Guid.NewGuid(), time.GetUtcNow(),
                new DeliveryCommand { Authority = key, AttemptId = attempt.Id });
            await journal.SendAsync([command], token);
        }
    }

    private async Task DeliverAsync(Guid intentId, CancellationToken token)
    {
        Guid attemptId = Guid.Empty;
        bool transportStarted = false;
        try
        {
            RevisionImage image = accepted.Current!;
            int number = image.Monitoring.Attempts.Values.Count(item => item.IntentId == intentId) + 1;
            attemptId = DeliveryCommands.AttemptId(intentId, number);
            bool invoked = await gate.ExecuteAttemptAsync(attemptId, async cancellation =>
            {
                // Между этой повторной проверкой и transport нет обращения к storage или другой очереди.
                RevisionImage current = accepted.Current!;
                MonitoringIntent intent = current.Monitoring.Intents[intentId];
                bool allowed = accepted.Available && recoveredVersion == accepted.RecoveryVersion
                    && recoveredTerm == state.Consensus?.Term && readiness.CanSend(state)
                    && intent.State == DeliveryState.AttemptStarted && DeliveryEligibility.Allows(current, intent)
                    && current.Monitoring.Attempts[attemptId].Outcome == AttemptOutcome.Unknown;
                if (!allowed)
                {
                    return;
                }

                AcceptedValue chat = current.Values[intent.ChatId];
                NotificationRecipient recipient = new(intent.ChatId, chat.Value.TelegramChatId.ToString(CultureInfo.InvariantCulture));
                string message = DeliveryMessage.Format(current, intent);
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                IAcceptedTelegramTransport transport = scope.ServiceProvider.GetRequiredService<IAcceptedTelegramTransport>();
                AttemptOutcome outcome = AttemptOutcome.Succeeded;
                long? messageId = null;
                string? failure = null;
                try
                {
                    // Scope resolution синхронен; последняя проверка отмены перед началом внешнего эффекта.
                    cancellation.ThrowIfCancellationRequested();
                    RevisionImage latest = accepted.Current!;
                    MonitoringIntent latestIntent = latest.Monitoring.Intents[intentId];
                    AcceptedDeliveryAttempt latestAttempt = latest.Monitoring.Attempts[attemptId];
                    if (!accepted.Available || recoveredVersion != accepted.RecoveryVersion
                        || latestIntent.State != DeliveryState.AttemptStarted || latestAttempt.Outcome != AttemptOutcome.Unknown
                        || !DeliveryEligibility.Allows(latest, latestIntent) || !gate.CanStartTransport(latestAttempt.Authority))
                    {
                        return;
                    }

                    transportStarted = true;
                    messageId = await transport.SendAcceptedAsync(recipient, message, cancellation);
                }
                catch (NotificationDeliveryException error)
                {
                    outcome = AttemptOutcome.Failed;
                    failure = error.Failure.ToString();
                    if (error.Failure == NotificationDeliveryFailure.RateLimited && error.RetryAfterSeconds.HasValue)
                    {
                        failure += "/" + error.RetryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
                    }
                }
                catch (OperationCanceledException)
                {
                    outcome = AttemptOutcome.Failed;
                    failure = "Ambiguous";
                }

                CapturedDeliveryResult result = new(attemptId, options.Value.NodeId, outcome, time.GetUtcNow(), messageId, failure);
                await CaptureAsync(result);
                // Не связывать фиксацию внешнего результата с уже отменённым leadership token.
                await ForwardAsync(result, CancellationToken.None);
            }, token);

            if (!transportStarted && (!invoked || accepted.Current!.Monitoring.Attempts.GetValueOrDefault(attemptId)?.Outcome == AttemptOutcome.Unknown))
            {
                AcceptedDeliveryAttempt? started = accepted.Current!.Monitoring.Attempts.GetValueOrDefault(attemptId);
                if (started is not null && started.Outcome == AttemptOutcome.Unknown)
                {
                    CapturedDeliveryResult cancelled = new(attemptId, options.Value.NodeId, AttemptOutcome.CancelledBeforeTransport,
                        time.GetUtcNow(), null, "CancelledBeforeTransport");
                    await CaptureAsync(cancelled);
                    await ForwardAsync(cancelled, CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            logger.LogWarning("Delivery deferred for {DeliveryId}, attempt {AttemptId}, term {Term}: {Category}.",
                intentId, attemptId, state.Consensus?.Term, "ResultOrTransportUnavailable");
        }
    }

    private async Task CaptureAsync(CapturedDeliveryResult result)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IDeliveryResultUnitOfWork work = scope.ServiceProvider.GetRequiredService<IDeliveryResultUnitOfWork>();
        await work.ExecuteInTransactionAsync(async token =>
        {
            CapturedDeliveryResult? existing = await work.Results.GetAsync(result.Id, token);
            if (existing is null)
            {
                work.Results.Create(result);
            }
            else if (existing.Outcome != result.Outcome || existing.CompletedAtUtc != result.CompletedAtUtc
                || existing.TelegramMessageId != result.TelegramMessageId || existing.FailureKind != result.FailureKind)
            {
                throw new InvalidDataException("CapturedDeliveryResultMismatch");
            }
        }, CancellationToken.None);
    }
}
