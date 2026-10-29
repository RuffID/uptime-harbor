using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public class PendingCommandService(IPendingControlUnitOfWork work, IUserAccessContext user,
    IAdministratorAccessPolicy policy, RevisionStateStore accepted,
    IRevisionPayloadProtection protection, IRevisionAudit audit, PendingAdmissionGate admission)
{
    public async Task<SubmissionReceipt> SubmitAsync(RevisionCommand proposal, Guid localNodeId,
        CancellationToken cancellationToken = default)
    {
        return await SubmitCoreAsync(proposal, localNodeId, null, cancellationToken);
    }

    private async Task<SubmissionReceipt> SubmitCoreAsync(RevisionCommand proposal, Guid localNodeId,
        Guid? confirmedTargetId, CancellationToken cancellationToken)
    {
        return await admission.ExecuteAsync(() => SavePendingAsync(proposal, localNodeId,
            confirmedTargetId, cancellationToken), cancellationToken);
    }

    private async Task<SubmissionReceipt> SavePendingAsync(RevisionCommand proposal, Guid localNodeId,
        Guid? confirmedTargetId, CancellationToken cancellationToken)
    {
        RequireAdministrator(proposal.OperationId, localNodeId);
        RevisionImage image = accepted.Current ?? throw new InvalidOperationException("AcceptedStateUnavailable");
        AcceptedAccount account = accepted.Find(user.UserId) ?? throw new InvalidOperationException("AccountNotAccepted");
        if (image.Results.TryGetValue(proposal.OperationId, out CommandResult? decision))
        {
            RevisionCommand retry = Stamp(proposal, image, localNodeId, decision.ActorRevision);
            EnsureDigest(decision.Digest, RevisionCodec.Digest(retry));
            return new SubmissionReceipt(proposal.OperationId, false, decision);
        }

        bool created = false;
        await work.ExecuteInTransactionAsync(async token =>
        {
            RequireAdministrator(proposal.OperationId, localNodeId);
            PendingOperation? existing = await work.PendingOperations.GetAsync(proposal.OperationId, token);
            if (existing is not null)
            {
                RevisionCommand retry = Stamp(proposal, image, localNodeId, existing.ActorUserRevision);
                EnsureDigest(existing.Digest, RevisionCodec.Digest(retry));
                return;
            }

            RevisionCommand command = Stamp(proposal, image, localNodeId, account.Revision);
            byte[] bytes = RevisionCodec.Encode(command);
            string digest = RevisionCodec.Digest(bytes);
            PendingBacklog backlog = await work.PendingOperations.GetBacklogAsync(token);
            admission.EnsureCapacity(backlog.Pending, command.Target is not null);

            if (command.Target is not null)
            {
                PendingOperation? target = await work.PendingOperations.GetAsync(command.Target.OperationId, token);
                if (target is null)
                {
                    throw new InvalidOperationException("PendingTargetMissing");
                }

                EnsureDigest(target.Digest, RevisionCodec.Digest(command.Target));
                if (target.State == PendingState.NeedsUserConfirmation && confirmedTargetId != target.Id)
                {
                    throw new InvalidOperationException("PendingRequiresReconciliation");
                }

                if (confirmedTargetId.HasValue)
                {
                    long revision = accepted.Current!.Values.GetValueOrDefault(target.EntityId)?.Revision ?? 0;
                    if (target.State != PendingState.NeedsUserConfirmation || command.Action != "Replace"
                        || command.Replacement?.BaseRevision != revision)
                    {
                        throw new InvalidOperationException("StaleConfirmation");
                    }
                }

                ControlKind kind = ControlKind.Cancel;
                if (command.Action == "Replace")
                {
                    kind = ControlKind.Replace;
                }

                if (target.State != PendingState.Resolved)
                {
                    target.RequestControl(command.OperationId, kind, command.Replacement?.OperationId);
                }
            }

            PendingOperation pending = new(new ClusterIdentity(image.ClusterId, image.Generation),
                new CommandIdentity(command.OperationId, localNodeId, user.UserId, null), $"{command.EntityKind}.{command.Action}",
                command.EntityId, command.BaseRevision, account.Revision, digest, protection.Protect(bytes), command.CreatedAtUtc);
            work.PendingOperations.Create(pending);
            created = true;
        }, cancellationToken);

        if (created)
        {
            audit.Record(proposal.OperationId, user.UserId, localNodeId, "LocalPending");
        }

        return new SubmissionReceipt(proposal.OperationId, true, null);
    }

    private RevisionCommand Stamp(RevisionCommand proposal, RevisionImage image, Guid localNodeId, long actorRevision)
    {
        RevisionCommand command = proposal with
        {
            ClusterId = image.ClusterId, ClusterGeneration = image.Generation,
            ActorUserId = user.UserId, ActorRevision = actorRevision, OriginNodeId = localNodeId
        };
        if (command.Replacement is not null)
        {
            command = command with
            {
                Replacement = command.Replacement with
                {
                    ClusterId = image.ClusterId, ClusterGeneration = image.Generation,
                    ActorUserId = user.UserId, ActorRevision = actorRevision, OriginNodeId = localNodeId
                }
            };
        }

        return command;
    }

    public SubmissionReceipt Find(Guid operationId)
    {
        RequireAdministrator();
        RevisionImage image = accepted.Current ?? throw new InvalidOperationException("AcceptedStateUnavailable");
        image.Results.TryGetValue(operationId, out CommandResult? decision);
        return new SubmissionReceipt(operationId, decision is null, decision);
    }

    public async Task ReconcileRestoredAsync(CancellationToken cancellationToken = default)
    {
        RevisionImage image = accepted.Current ?? throw new InvalidOperationException("AcceptedStateUnavailable");
        await ReconcileAsync(work, image, cancellationToken);
    }

    public static async Task ReconcileAsync(IPendingControlUnitOfWork work, RevisionImage image, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (true)
        {
            IReadOnlyList<PendingOperation> page = await work.PendingOperations.GetPageAsync(offset, 100, false, cancellationToken);
            if (page.Count == 0)
            {
                return;
            }

            await work.ExecuteInTransactionAsync(_ =>
            {
                foreach (PendingOperation pending in page)
                {
                    if (image.Results.TryGetValue(pending.Id, out CommandResult? decision))
                    {
                        pending.Reconcile(ToDecision(image, decision));
                    }
                    else
                    {
                        pending.RequireConfirmationAfterRestore();
                    }
                }

                return Task.CompletedTask;
            }, cancellationToken);
            offset += page.Count;
        }
    }

    public async Task<SubmissionReceipt> ConfirmRestoredAsync(Guid oldOperationId, RevisionCommand renewed,
        Guid localNodeId, CancellationToken cancellationToken = default)
    {
        RequireAdministrator();
        PendingOperation old = await work.PendingOperations.GetAsync(oldOperationId, cancellationToken)
            ?? throw new InvalidOperationException("PendingMissing");
        RevisionImage image = accepted.Current!;
        if (image.Results.TryGetValue(old.Id, out CommandResult? decision))
        {
            await work.ExecuteInTransactionAsync(_ =>
            {
                old.Reconcile(ToDecision(image, decision));
                return Task.CompletedTask;
            }, cancellationToken);
            return new SubmissionReceipt(old.Id, false, decision);
        }

        if (old.State != PendingState.NeedsUserConfirmation || renewed.Action != "Replace"
            || renewed.Target?.OperationId != old.Id || renewed.Replacement is null)
        {
            throw new InvalidOperationException("ExplicitReplacementRequired");
        }

        long currentRevision = image.Values.GetValueOrDefault(old.EntityId)?.Revision ?? 0;
        if (renewed.Replacement.BaseRevision != currentRevision)
        {
            throw new InvalidOperationException("StaleConfirmation");
        }

        // Исходная копия остаётся удержанной; control и новое значение сохраняются одной транзакцией.
        return await SubmitCoreAsync(renewed, localNodeId, oldOperationId, cancellationToken);
    }

    private void RequireAdministrator(Guid? operationId = null, Guid? localNodeId = null)
    {
        ServiceError? error = policy.GetAccessError();
        if (error is not null || !AcceptedAdministratorAccessPolicy.IsActiveAdministrator(accepted.Find(user.UserId)))
        {
            if (operationId.HasValue && localNodeId.HasValue)
            {
                audit.Record(operationId.Value, user.UserId, localNodeId.Value, "Denied");
            }

            throw new UnauthorizedAccessException("AcceptedAdministratorRequired");
        }
    }

    private static void EnsureDigest(string expected, string actual)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException("IdempotencyKeyReuse");
        }
    }

    public static OperationDecision ToDecision(RevisionImage image, CommandResult result)
    {
        return new OperationDecision(result.OperationId, result.Digest, result.Outcome,
            new JournalPosition(new ClusterIdentity(image.ClusterId, image.Generation), result.Index, result.Term),
            result.AppliedRevision, result.Reason, result.ConflictId, result.ReplacedBy);
    }
}
