using UptimeHarbor.Application.Common;

namespace UptimeHarbor.Application.Security;

public interface IAdministratorAccessPolicy
{
    ServiceError? GetAccessError();
}
