using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

public class ProjectionApplyUnitOfWork(
    IUptimeHarborDbContextSession session,
    IProjectionRepository repository)
    : UptimeHarborDbUnitOfWork(session), IProjectionApplyUnitOfWork
{
    public async Task<bool> ApplyAsync(ProjectionWriteSet changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        StorageGuards.Id(changes.NodeId, nameof(changes.NodeId));
        ArgumentOutOfRangeException.ThrowIfNegative(changes.ExpectedCursorIndex);
        bool applied = false;

        await ExecuteInTransactionAsync(async token =>
        {
            JournalPosition position = changes.Receipt.GetPosition();
            ProjectionCursor? cursor = await repository.GetCursorAsync(changes.NodeId, token);
            AppliedJournalReceipt? existing = await repository.GetReceiptAsync(position, token);
            if (existing is not null)
            {
                if (existing.Id != changes.Receipt.Id || existing.Term != position.Term
                    || existing.Digest != changes.Receipt.Digest || cursor is null || cursor.LogIndex < position.LogIndex)
                {
                    throw new InvalidOperationException("Projection receipt is inconsistent with the committed prefix.");
                }

                position.EnsureSameCluster(new ClusterIdentity(cursor.ClusterId, cursor.ClusterGeneration));
                return;
            }

            if (cursor is null)
            {
                if (changes.ExpectedCursorIndex != 0)
                {
                    throw new InvalidOperationException("A missing projection requires snapshot recovery.");
                }

                cursor = new ProjectionCursor(changes.NodeId, position.Cluster);
                repository.AddCursor(cursor);
            }

            cursor.Advance(changes.ExpectedCursorIndex, position, changes.Receipt.Digest);
            await repository.StageAsync(changes, token);
            repository.AddReceipt(changes.Receipt);
            applied = true;
        }, cancellationToken);

        return applied;
    }
}
