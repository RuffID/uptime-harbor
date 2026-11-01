using UptimeHarbor.Application.Common;

namespace UptimeHarbor.Application.Security;

public class AcceptedAdministratorAccessPolicy(IUserAccessContext user, IAcceptedAccountReader accounts)
    : IAdministratorAccessPolicy
{
    public ServiceError? GetAccessError()
    {
        if (!user.IsAuthenticated || user.UserId == Guid.Empty)
        {
            return new ServiceError(ServiceErrorType.Unauthorized, "Требуется аутентификация.");
        }

        if (!IsActiveAdministrator(accounts.Find(user.UserId)))
        {
            return new ServiceError(ServiceErrorType.Forbidden, "Требуется принятый активный администратор.");
        }

        return null;
    }

    public static bool IsActiveAdministrator(AcceptedAccount? account)
    {
        if (account is null || account.UserId == Guid.Empty || account.Revision <= 0)
        {
            return false;
        }

        return account.IsEnabled && account.IsAdministrator && !account.IsDeleted;
    }
}
