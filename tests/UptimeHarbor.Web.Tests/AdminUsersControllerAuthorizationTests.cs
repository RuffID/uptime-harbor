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

    [Fact]
    public void Frontend_HidesDisableAndDeleteForCurrentAdministrator()
    {
        string repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        string source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "UptimeHarbor.Web",
            "wwwroot",
            "ts",
            "pages",
            "admin",
            "users.ts"));

        Assert.Contains("selected.id === currentUserId", source, StringComparison.Ordinal);
        Assert.Contains("userEnabled.disabled", source, StringComparison.Ordinal);
        Assert.Contains("delete-user", source, StringComparison.Ordinal);
    }
}
