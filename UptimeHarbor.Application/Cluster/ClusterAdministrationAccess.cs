using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;

namespace UptimeHarbor.Application.Cluster;

public class ClusterAdministrationAccess(IAdministratorAccessPolicy policy, IUserAccessContext user,
    RevisionStateStore state)
{
    public ServiceError? GetError()
    {
        ServiceError? error = policy.GetAccessError();
        if (error is not null)
        {
            return error;
        }

        if (!AcceptedAdministratorAccessPolicy.IsActiveAdministrator(state.Find(user.UserId)))
        {
            return new ServiceError(ServiceErrorType.Forbidden, "Требуется активный принятый администратор.");
        }

        return null;
    }
}
