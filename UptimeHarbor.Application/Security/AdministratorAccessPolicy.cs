using UptimeHarbor.Application.Common;

namespace UptimeHarbor.Application.Security;

public class AdministratorAccessPolicy(IUserAccessContext accessContext)
    : IAdministratorAccessPolicy
{
    public ServiceError? GetAccessError()
    {
        if (!accessContext.IsAuthenticated)
        {
            return new ServiceError(ServiceErrorType.Unauthorized, "Требуется аутентификация.");
        }

        return !accessContext.IsEnabled || !accessContext.IsAdministrator
            ? new ServiceError(
                ServiceErrorType.Forbidden,
                "Операция доступна только администратору.")
            : null;
    }
}
