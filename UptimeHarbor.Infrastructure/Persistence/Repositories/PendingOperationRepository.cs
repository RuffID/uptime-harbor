using EFCoreLibrary.Abstractions.Database.Repository.Base;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Repositories;

public class PendingOperationRepository(
    UptimeHarborDbContext context,
    IContextGetItemByPredicateRepository<PendingOperation, UptimeHarborDbContext> reader,
    IContextCreateItemRepository<PendingOperation, UptimeHarborDbContext> writer) : IPendingOperationRepository
{
    public async Task<IReadOnlyList<PendingOperation>> GetDueAsync(bool priority, DateTimeOffset nowUtc,
        int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        IQueryable<PendingOperation> query = context.Set<PendingOperation>()
            .Where(item => item.State != PendingState.NeedsUserConfirmation && item.State != PendingState.Resolved
                && item.RequestedControlId == null);
        query = query.Where(item => item.NextAttemptAtUtc == null || item.NextAttemptAtUtc <= nowUtc);
        if (priority)
        {
            query = query.Where(item => item.Kind.StartsWith("Account.") || item.Kind.StartsWith("Node.") || item.Kind.EndsWith(".Delete")
                || item.Kind.EndsWith(".Cancel") || item.Kind.EndsWith(".Replace"));
        }
        else
        {
            query = query.Where(item => !item.Kind.StartsWith("Account.") && !item.Kind.StartsWith("Node.") && !item.Kind.EndsWith(".Delete")
                && !item.Kind.EndsWith(".Cancel") && !item.Kind.EndsWith(".Replace"));
        }

        return await query.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<PendingBacklog> GetBacklogAsync(CancellationToken cancellationToken = default)
    {
        IQueryable<PendingOperation> pending = context.Set<PendingOperation>().Where(item => item.State != PendingState.Resolved);
        long count = await pending.LongCountAsync(cancellationToken);
        long confirmation = await pending.LongCountAsync(item => item.State == PendingState.NeedsUserConfirmation, cancellationToken);
        DateTimeOffset? oldest = await pending.MinAsync(item => (DateTimeOffset?)item.CreatedAtUtc, cancellationToken);
        return new PendingBacklog(count, confirmation, oldest);
    }

    public Task<PendingOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        return reader.GetItemByPredicateAsync(item => item.Id == operationId, asNoTracking: false, ct: cancellationToken);
    }

    public void Create(PendingOperation operation)
    {
        writer.Create(operation);
    }

    public async Task<IReadOnlyList<PendingOperation>> GetPageAsync(int offset, int limit, bool readyOnly,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        IQueryable<PendingOperation> query = context.Set<PendingOperation>();
        if (readyOnly)
        {
            query = query.Where(item => item.State != PendingState.NeedsUserConfirmation
                && item.State != PendingState.Resolved && item.RequestedControlId == null);
        }

        return await query.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id)
            .Skip(offset).Take(limit).ToListAsync(cancellationToken);
    }
}
