using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Web.Controllers;
using Xunit;

namespace UptimeHarbor.Tests.Web;

public class AdminNotificationsControllerAuthorizationTests
{
    [Fact]
    public void Controller_RequiresAdministratorRole()
    {
        AuthorizeAttribute attribute = Assert.Single(
            typeof(AdminNotificationsController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());

        Assert.Equal(ApplicationRoles.ADMINISTRATOR, attribute.Roles);
    }

    [Fact]
    public void MutatingActions_RequireAntiforgeryValidation()
    {
        string[] actionNames =
        [
            nameof(AdminNotificationsController.CreateChatAsync),
            nameof(AdminNotificationsController.UpdateChatAsync),
            nameof(AdminNotificationsController.DeleteChatAsync),
            nameof(AdminNotificationsController.SendTestMessageAsync),
            nameof(AdminNotificationsController.CreateBindingAsync),
            nameof(AdminNotificationsController.DeleteBindingAsync)
        ];

        foreach (string actionName in actionNames)
        {
            System.Reflection.MethodInfo method = Assert.Single(
                typeof(AdminNotificationsController).GetMethods(),
                candidate => candidate.Name == actionName);
            Assert.Single(method.GetCustomAttributes(
                typeof(ValidateAntiForgeryTokenAttribute),
                inherit: true));
        }
    }

    [Fact]
    public void Frontend_UsesAjaxDomApiAndDoesNotExposeBotToken()
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
            "notifications.ts"));
        string page = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "UptimeHarbor.Web",
            "Pages",
            "Admin",
            "Notifications.cshtml"));

        Assert.Contains("sendJsonRequest", source, StringComparison.Ordinal);
        Assert.Contains("document.createElement", source, StringComparison.Ordinal);
        Assert.Contains("data-loading", page, StringComparison.Ordinal);
        Assert.Contains("data-chat-empty", page, StringComparison.Ordinal);
        Assert.Contains("data-page-error", page, StringComparison.Ordinal);
        Assert.Contains("window.confirm", source, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", source, StringComparison.Ordinal);
        Assert.DoesNotContain("location.reload", source, StringComparison.Ordinal);
        Assert.DoesNotContain("botToken", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("botToken", page, StringComparison.OrdinalIgnoreCase);
    }
}
