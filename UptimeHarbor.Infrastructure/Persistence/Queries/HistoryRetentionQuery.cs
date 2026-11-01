using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Queries;

// Только чтение причин защиты; очистка и её согласование не запускаются этим компонентом.
public class HistoryRetentionQuery(UptimeHarborDbContext context) : IHistoryRetentionQuery
{
    public async Task<HistoryProtection> GetIncidentProtectionAsync(Guid incidentId,
        CancellationToken cancellationToken = default)
    {
        SharedIncident? incident = await context.Set<SharedIncident>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == incidentId, cancellationToken);
        if (incident is null)
        {
            throw new InvalidOperationException("Incident does not exist.");
        }

        HistoryProtection protection = HistoryProtection.None;
        if (incident.CompletedAtUtc is null)
        {
            protection |= HistoryProtection.OpenIncident;
        }

        IQueryable<DeliveryIntent> intents = context.Set<DeliveryIntent>().Where(item => item.IncidentId == incidentId);
        bool unfinished = await intents.AnyAsync(item => item.State == DeliveryState.Planned
            || item.State == DeliveryState.AttemptStarted || item.State == DeliveryState.RetryDue, cancellationToken);
        if (unfinished)
        {
            protection |= HistoryProtection.UnfinishedDelivery;
        }

        bool unknown = await context.Set<DeliveryAttempt>()
            .AnyAsync(attempt => attempt.Outcome == AttemptOutcome.Unknown
                && intents.Any(intent => intent.Id == attempt.IntentId), cancellationToken);
        if (unknown)
        {
            protection |= HistoryProtection.UnknownAttempt;
        }

        bool recovery = await context.Set<RecoveryEntitlement>()
            .AnyAsync(item => item.IncidentId == incidentId && item.ConsumedAtUtc == null, cancellationToken);
        if (recovery)
        {
            protection |= HistoryProtection.RecoveryEntitlement;
        }

        // Даже успешную сводку сначала нужно безопасно освободить от ссылок; FK запрещает обрыв истории.
        bool summary = await context.Set<SummarySource>()
            .AnyAsync(source => intents.Any(intent => intent.Id == source.SourceIntentId), cancellationToken);
        if (summary)
        {
            protection |= HistoryProtection.SummaryReference;
        }

        bool assembly = await context.Set<ObservationFrontier>()
            .AnyAsync(frontier => frontier.TargetId == incident.TargetId
                && frontier.CheckGeneration == incident.CheckGeneration && frontier.IsHistoricalAssemblyOpen,
                cancellationToken);
        if (assembly)
        {
            protection |= HistoryProtection.ObservationAssembly;
        }

        return protection;
    }
}
