using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;

namespace UptimeHarbor.Application.Cluster;

public class MembershipCoordinator(IUserAccessContext user, IClusterAdministrationState accepted,
    IMembershipProtocol protocol, SenderAuthorityGate gate, IClusterAudit audit, TimeProvider timeProvider,
    IAdministratorAccessPolicy policy, IAcceptedAccountReader? accounts = null)
{
    public async Task<ServiceResult> ExecuteAsync(Guid changeId, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated || user.UserId == Guid.Empty)
        {
            return ServiceResult.Fail(ServiceErrorType.Unauthorized, "Требуется вход администратора.");
        }

        if (policy.GetAccessError() is not null)
        {
            audit.Membership(user.UserId, Guid.Empty, changeId, "Denied");
            return ServiceResult.Fail(ServiceErrorType.Forbidden, "Требуется активный администратор.");
        }

        return await ExecuteCoreAsync(user.UserId, changeId, true, cancellationToken);
    }

    public async Task<ServiceResult> ExecuteAcceptedAsync(Guid actorUserId, Guid changeId, CancellationToken cancellationToken)
    {
        if (accounts is null || !AcceptedAdministratorAccessPolicy.IsActiveAdministrator(accounts.Find(actorUserId)))
        {
            return ServiceResult.Fail(ServiceErrorType.Forbidden, "Требуется активный принятый администратор.");
        }

        return await ExecuteCoreAsync(actorUserId, changeId, false, cancellationToken);
    }

    private async Task<ServiceResult> ExecuteCoreAsync(Guid actorId, Guid changeId, bool userAction, CancellationToken cancellationToken)
    {
        AcceptedMembershipChange? change = await accepted.FindAuthorizedChangeAsync(actorId, changeId, cancellationToken);
        if (change is null || change.ChangeId != changeId || change.ActorUserId != actorId || !change.ActiveAdministratorConfirmed
            || !change.TrustConfirmed || !change.PreviousChangeReconciled)
        {
            audit.Membership(actorId, Guid.Empty, changeId, "Unconfirmed");
            return ServiceResult.Fail(ServiceErrorType.Forbidden, "Принятое полномочие администратора не подтверждено.");
        }

        MembershipSnapshot? before = await protocol.ReadAsync(cancellationToken);
        if (before is null)
        {
            return ServiceResult.Fail(ServiceErrorType.Conflict, "Требуется сверка принятого состава.");
        }

        if (before.Version > change.ExpectedMembershipVersion)
        {
            if ((userAction && policy.GetAccessError() is not null)
                || await accepted.FindAuthorizedChangeAsync(actorId, changeId, cancellationToken) != change)
            {
                return ServiceResult.Fail(ServiceErrorType.Forbidden, "Требуется активный принятый администратор.");
            }

            bool present = before.Voters.TryGetValue(change.TargetNodeId, out Uri? reconciledAddress)
                && reconciledAddress == change.Address;
            if (present != change.Remove && await accepted.ConfirmProtocolResultAsync(changeId, before.Version, cancellationToken))
            {
                audit.Membership(actorId, change.TargetNodeId, changeId, "Reconciled");
                return ServiceResult.Ok();
            }

            return ServiceResult.Fail(ServiceErrorType.Conflict, "Требуется сверка принятого состава.");
        }

        if (before.Version != change.ExpectedMembershipVersion)
        {
            return ServiceResult.Fail(ServiceErrorType.Conflict, "Требуется сверка принятого состава.");
        }

        if (change.Remove && before.Voters.Count <= 1)
        {
            return ServiceResult.Fail(ServiceErrorType.Conflict, "Нельзя удалить последнего участника.");
        }

        bool duplicateIdentity = before.Voters.TryGetValue(change.TargetNodeId, out Uri? existing)
            && existing != change.Address;
        if (duplicateIdentity)
        {
            return ServiceResult.Fail(ServiceErrorType.Conflict, "NodeId уже связан с другим адресом.");
        }

        bool duplicateAddress = before.Voters.Any(voter => voter.Key != change.TargetNodeId && voter.Value == change.Address);
        if (duplicateAddress || (change.Remove && existing != change.Address))
        {
            return ServiceResult.Fail(ServiceErrorType.Conflict, "Адрес не соответствует принятому участнику.");
        }

        gate.SuspendMembership();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(22), timeProvider, cancellationToken);
            if ((userAction && policy.GetAccessError() is not null)
                || await accepted.FindAuthorizedChangeAsync(actorId, changeId, cancellationToken) != change)
            {
                audit.Membership(actorId, change.TargetNodeId, change.ChangeId, "Denied");
                return ServiceResult.Fail(ServiceErrorType.Forbidden, "Требуется активный принятый администратор.");
            }

            bool committed = await protocol.ChangeAsync(change, cancellationToken);
            MembershipSnapshot? after = await protocol.ReadAsync(cancellationToken);
            bool contains = false;
            if (after is not null && after.Voters.TryGetValue(change.TargetNodeId, out Uri? address))
            {
                contains = address == change.Address;
            }

            bool expectedPresence = contains != change.Remove;
            if (!committed || after is null || after.Version <= before.Version || !expectedPresence)
            {
                audit.Membership(actorId, change.TargetNodeId, change.ChangeId, "Unknown");
                return ServiceResult.Fail(ServiceErrorType.Conflict, "Результат изменения состава требует сверки.");
            }

            bool confirmed = await accepted.ConfirmProtocolResultAsync(change.ChangeId, after.Version, cancellationToken);
            if (!confirmed)
            {
                audit.Membership(actorId, change.TargetNodeId, change.ChangeId, "Unknown");
                return ServiceResult.Fail(ServiceErrorType.Conflict, "Фиксация результата состава не подтверждена.");
            }

            audit.Membership(actorId, change.TargetNodeId, change.ChangeId, "Committed");
            return ServiceResult.Ok();
        }
        catch (OperationCanceledException)
        {
            audit.Membership(actorId, change.TargetNodeId, change.ChangeId, "Cancelled");
            throw;
        }
        catch
        {
            audit.Membership(actorId, change.TargetNodeId, change.ChangeId, "Unknown");
            throw;
        }
    }
}
