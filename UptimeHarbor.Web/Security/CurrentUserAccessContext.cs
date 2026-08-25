using System.Security.Claims;
using UptimeHarbor.Application.Security;

namespace UptimeHarbor.Web.Security;

public class CurrentUserAccessContext(IHttpContextAccessor httpContextAccessor)
    : IUserAccessContext
{
    private ClaimsPrincipal User => httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal();

    public bool IsAuthenticated => User.Identity?.IsAuthenticated == true;

    public bool IsAdministrator => User.IsInRole(ApplicationRoles.ADMINISTRATOR);

    public bool IsEnabled => IsAuthenticated;

    public Guid UserId
    {
        get
        {
            string? value = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out Guid userId) ? userId : Guid.Empty;
        }
    }
}
