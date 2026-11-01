using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

/// <summary>Создаёт только локальную идентичность в проверенной пустой проекции; аккаунты создаёт журнал.</summary>
public class ClusterLocalInitializationUnitOfWork(IUptimeHarborDbContextSession session, UptimeHarborDbContext context)
    : UptimeHarborDbUnitOfWork(session)
{
    public async Task InitializeAsync(RecoveryAdmission admission, string walIdentity, CancellationToken token)
    {
        await ExecuteInTransactionAsync(async cancellation =>
        {
            List<LocalNodeState> states = await context.Set<LocalNodeState>().ToListAsync(cancellation);
            if (states.Count > 0)
            {
                LocalNodeState? local = states.SingleOrDefault(item => item.Id == admission.NodeId);
                if (states.Count != 1 || local is null || local.ClusterId != admission.ClusterId
                    || local.ClusterGeneration != admission.Generation || local.StorageGeneration != admission.StorageGeneration
                    || local.WalStoreIdentity != walIdentity)
                {
                    throw new InvalidDataException("LocalStorageIdentityMismatch");
                }

                return;
            }

            foreach (var type in SnapshotSchema.Entities(context))
            {
                if ((await SnapshotSchema.ReadAsync(context, type.ClrType, cancellation)).Count != 0)
                {
                    throw new InvalidDataException("NonemptyUnrelatedJoinForbidden");
                }
            }

            if (await context.Set<ProjectionCursor>().AnyAsync(cancellation))
            {
                throw new InvalidDataException("UnrelatedProjectionCursor");
            }

            context.Add(new LocalNodeState(admission.NodeId, new ClusterIdentity(admission.ClusterId, admission.Generation),
                admission.StorageGeneration, Guid.NewGuid(), walIdentity));
        }, token);
    }
}
