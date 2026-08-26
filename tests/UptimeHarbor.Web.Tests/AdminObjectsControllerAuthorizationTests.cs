using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Web.Controllers;
using Xunit;

namespace UptimeHarbor.Tests.Web;

public class AdminObjectsControllerAuthorizationTests
{
    [Fact]
    public void Controller_RequiresAdministratorRole()
    {
        AuthorizeAttribute attribute = Assert.Single(
            typeof(AdminObjectsController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());

        Assert.Equal(ApplicationRoles.ADMINISTRATOR, attribute.Roles);
    }

    [Fact]
    public void MutatingActions_RequireAntiforgeryValidation()
    {
        string[] actionNames =
        [
            nameof(AdminObjectsController.CreateServerAsync),
            nameof(AdminObjectsController.UpdateServerAsync),
            nameof(AdminObjectsController.CreateContainerAsync),
            nameof(AdminObjectsController.UpdateContainerAsync),
            nameof(AdminObjectsController.DeleteServerAsync),
            nameof(AdminObjectsController.DeleteContainerAsync)
        ];

        foreach (string actionName in actionNames)
        {
            System.Reflection.MethodInfo method = Assert.Single(
                typeof(AdminObjectsController).GetMethods(),
                candidate => candidate.Name == actionName);
            Assert.Single(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true));
        }
    }
}
