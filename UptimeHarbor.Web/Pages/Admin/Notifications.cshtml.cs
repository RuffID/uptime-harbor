using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using UptimeHarbor.Application.Security;

namespace UptimeHarbor.Web.Pages.Admin;

[Authorize(Roles = ApplicationRoles.ADMINISTRATOR)]
public class NotificationsModel : PageModel;
