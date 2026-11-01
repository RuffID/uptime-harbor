using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Ограниченная durable очередь. Retry не создаёт новую операцию; timeout не является отказом команды.</summary>
public class PendingReplicationPump(IServiceScopeFactory scopes, IRevisionTransport transport,
    RevisionStateStore accepted, IClusterStateView runtime, IOptions<ClusterOptions> configured,
    TimeProvider timeProvider, IRevisionAudit audit)
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private long? lastConfigurationCycle;
    private int reconciliationOffset;
    private string? delayReason;
    private DateTimeOffset? lastSuccessfulSynchronizationAtUtc;
    private ReplicationProgress progress = new(0, 0, 0, 0, 0, 0, null, "RecoveryUnconfirmed");
    public ReplicationProgress Progress => Volatile.Read(ref progress);

    public async Task RunOnceAsync(CancellationToken token)
    {
        await serial.WaitAsync(token);
        try
        {
            if (!accepted.Available || !runtime.Accepted.CaughtUp)
            {
                await RefreshProgressAsync("RecoveryUnconfirmed", token);
                return;
            }

            await ReconcileKnownAsync(token);
            await ProcessAsync(true, token);
            bool configurationDue = !lastConfigurationCycle.HasValue
                || timeProvider.GetElapsedTime(lastConfigurationCycle.Value) >= TimeSpan.FromSeconds(configured.Value.ConfigurationIntervalSeconds);
            if (configurationDue)
            {
                await ProcessAsync(false, token);
                lastConfigurationCycle = timeProvider.GetTimestamp();
            }

            await RefreshProgressAsync(delayReason, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or OperationCanceledException
            or InvalidOperationException or System.Text.Json.JsonException)
        {
            // Нет raw exception/payload. Durable записи сохраняют AwaitingDecision и retry deadline.
            delayReason = "PeerOrDecisionUnavailable";
            await RefreshProgressAsync(delayReason, token);
        }
        finally
        {
            serial.Release();
        }
    }

    private async Task ReconcileKnownAsync(CancellationToken token)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IPendingControlUnitOfWork work = scope.ServiceProvider.GetRequiredService<IPendingControlUnitOfWork>();
        IReadOnlyList<PendingOperation> page = await work.PendingOperations.GetPageAsync(reconciliationOffset, 100, false, token);
        RevisionImage image = accepted.Current!;
        foreach (PendingOperation pending in page)
        {
            if (image.Results.TryGetValue(pending.Id, out CommandResult? result))
            {
                await ResolveAsync(work, pending, result, image, token);
            }
        }

        reconciliationOffset += page.Count;
        if (page.Count < 100)
        {
            reconciliationOffset = 0;
        }
    }

    private async Task ProcessAsync(bool priority, CancellationToken token)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IPendingControlUnitOfWork work = scope.ServiceProvider.GetRequiredService<IPendingControlUnitOfWork>();
        IRevisionPayloadProtection protection = scope.ServiceProvider.GetRequiredService<IRevisionPayloadProtection>();
        ClusterOptions options = configured.Value;
        DateTimeOffset now = timeProvider.GetUtcNow();
        IReadOnlyList<PendingOperation> due = await work.PendingOperations.GetDueAsync(priority, now, options.BatchMaxEntries, token);
        List<RevisionCommand> commands = [];
        List<PendingOperation> pending = [];
        int bytes = 2;
        foreach (PendingOperation item in due)
        {
            RevisionImage image = accepted.Current!;
            if (image.Results.TryGetValue(item.Id, out CommandResult? known))
            {
                await ResolveAsync(work, item, known, image, token);
                continue;
            }

            byte[] payload = protection.Unprotect(item.ProtectedPayload);
            RevisionCommand command = RevisionCodec.Decode(payload);
            if (RevisionCodec.Digest(payload) != item.Digest || command.OperationId != item.Id
                || command.OriginNodeId != options.NodeId || command.ActorUserId != item.ActorUserId)
            {
                throw new InvalidDataException("PendingIdentityMismatch");
            }

            if (payload.Length > options.CommandMaxBytes || bytes + payload.Length + 1 > options.BatchMaxBytes)
            {
                delayReason = "BatchCapacityLimit";
                break;
            }

            bytes += payload.Length + 1;
            commands.Add(command);
            pending.Add(item);
        }

        if (commands.Count == 0)
        {
            return;
        }

        await work.ExecuteInTransactionAsync(_ =>
        {
            foreach (PendingOperation item in pending)
            {
                int[] delays = [1, 2, 5, 10, 30];
                int delay = delays[Math.Min(item.SubmissionAttempts, delays.Length - 1)];
                double jitter = Random.Shared.NextDouble() * 0.2;
                item.MarkTransmissionStarted(now.AddSeconds(delay * (1 + jitter)));
            }

            return Task.CompletedTask;
        }, token);
        IReadOnlyList<CommandResult> results = await transport.SendAsync(commands, token);
        ValidateResults(commands, results);
        RevisionImage current = accepted.Current!;
        await work.ExecuteInTransactionAsync(async cancellation =>
        {
            foreach (PendingOperation item in pending)
            {
                CommandResult result = results.Single(result => result.OperationId == item.Id);
                item.Reconcile(PendingCommandService.ToDecision(current, result));
                RevisionCommand command = commands.Single(command => command.OperationId == item.Id);
                if (command.Target is not null)
                {
                    PendingOperation? target = await work.PendingOperations.GetAsync(command.Target.OperationId, cancellation);
                    if (target is not null && current.Results.TryGetValue(target.Id, out CommandResult? decision))
                    {
                        target.Reconcile(PendingCommandService.ToDecision(current, decision));
                    }
                }
            }
        }, token);

        foreach (PendingOperation item in pending)
        {
            CommandResult result = results.Single(result => result.OperationId == item.Id);
            RecordResult(item, result);
        }

        delayReason = null;
        lastSuccessfulSynchronizationAtUtc = timeProvider.GetUtcNow();
    }

    private async Task ResolveAsync(IPendingControlUnitOfWork work, PendingOperation pending,
        CommandResult result, RevisionImage image, CancellationToken token)
    {
        if (pending.State == PendingState.Resolved)
        {
            return;
        }

        await work.ExecuteInTransactionAsync(_ =>
        {
            pending.Reconcile(PendingCommandService.ToDecision(image, result));
            return Task.CompletedTask;
        }, token);
        RecordResult(pending, result);
    }

    private void RecordResult(PendingOperation pending, CommandResult result)
    {
        string outcome = result.Outcome.ToString();
        if (result.Reason == "AlreadyDecided")
        {
            outcome = "AlreadyDecided";
        }

        if (pending.Kind.StartsWith("Account.", StringComparison.Ordinal))
        {
            audit.RecordAccount(pending.Id, pending.ActorUserId!.Value, pending.EntityId, pending.OriginNodeId, outcome);
        }
        else
        {
            audit.Record(pending.Id, pending.ActorUserId!.Value, pending.OriginNodeId, outcome);
        }
    }

    public static void ValidateResults(IReadOnlyList<RevisionCommand> commands, IReadOnlyList<CommandResult> results)
    {
        if (results.Count != commands.Count || results.Select(result => result.OperationId).Distinct().Count() != results.Count)
        {
            throw new InvalidDataException("InvalidDecisionBatch");
        }

        foreach (RevisionCommand command in commands)
        {
            CommandResult? result = results.SingleOrDefault(result => result.OperationId == command.OperationId);
            if (result is null || result.Digest != RevisionCodec.Digest(command) || result.Index <= 0 || result.Term <= 0)
            {
                throw new InvalidDataException("DecisionBatchMismatch");
            }
        }
    }

    private async Task RefreshProgressAsync(string? reason, CancellationToken token)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        PendingBacklog backlog = await scope.ServiceProvider.GetRequiredService<IPendingOperationRepository>().GetBacklogAsync(token);
        RevisionImage? image = accepted.Current;
        long applied = image?.Index ?? 0;
        long committed = runtime.Consensus?.CommittedIndex ?? applied;
        long rejected = image?.Results.Values.LongCount(result => result.Outcome is OperationOutcome.Rejected or OperationOutcome.Conflict) ?? 0;
        Volatile.Write(ref progress, new(backlog.Pending, backlog.Confirmation, rejected, applied, committed,
            Math.Max(0, committed - applied), backlog.OldestAtUtc, reason)
        {
            LastSuccessfulSynchronizationAtUtc = lastSuccessfulSynchronizationAtUtc
        });
    }
}
