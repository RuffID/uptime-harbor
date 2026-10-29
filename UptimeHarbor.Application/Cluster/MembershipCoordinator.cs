using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;

namespace UptimeHarbor.Application.Cluster;

public class MembershipCoordinator(IUserAccessContext user, IClusterAdministrationState accepted,
    IMembershipProtocol protocol, SenderAuthorityGate gate, IClusterAudit audit, TimeProvider timeProvider,
    IAdministratorAccessPolicy policy)
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

        AcceptedMembershipChange? change = await accepted.FindAuthorizedChangeAsync(user.UserId, changeId, cancellationToken);
        if (change is null || change.ChangeId != changeId || change.ActorUserId != user.UserId || !change.ActiveAdministratorConfirmed
            || !change.TrustConfirmed || !change.PreviousChangeReconciled)
        {
            audit.Membership(user.UserId, Guid.Empty, changeId, "Unconfirmed");
            return ServiceResult.Fail(ServiceErrorType.Forbidden, "Принятое полномочие администратора не подтверждено.");
        }

        MembershipSnapshot? before = await protocol.ReadAsync(cancellationToken);
        if (before is null || before.Version != change.ExpectedMembershipVersion)
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
            if (policy.GetAccessError() is not null)
            {
                audit.Membership(user.UserId, change.TargetNodeId, change.ChangeId, "Denied");
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
                audit.Membership(user.UserId, change.TargetNodeId, change.ChangeId, "Unknown");
                return ServiceResult.Fail(ServiceErrorType.Conflict, "Результат изменения состава требует сверки.");
            }

            bool confirmed = await accepted.ConfirmProtocolResultAsync(change.ChangeId, after.Version, cancellationToken);
            if (!confirmed)
            {
                audit.Membership(user.UserId, change.TargetNodeId, change.ChangeId, "Unknown");
                return ServiceResult.Fail(ServiceErrorType.Conflict, "Фиксация результата состава не подтверждена.");
            }

            audit.Membership(user.UserId, change.TargetNodeId, change.ChangeId, "Committed");
            return ServiceResult.Ok();
        }
        catch (OperationCanceledException)
        {
            audit.Membership(user.UserId, change.TargetNodeId, change.ChangeId, "Cancelled");
            throw;
        }
        catch
        {
            audit.Membership(user.UserId, change.TargetNodeId, change.ChangeId, "Unknown");
            throw;
        }
    }
}
