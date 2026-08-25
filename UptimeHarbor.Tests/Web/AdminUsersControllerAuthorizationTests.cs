using Microsoft.AspNetCore.Authorization;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Web.Controllers;
using Xunit;

namespace UptimeHarbor.Tests.Web;

public class AdminUsersControllerAuthorizationTests
{
    [Fact]
    public void Controller_RequiresAdministratorRole()
    {
        AuthorizeAttribute attribute = Assert.Single(
            typeof(AdminUsersController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());

        Assert.Equal(ApplicationRoles.ADMINISTRATOR, attribute.Roles);
    }
}
