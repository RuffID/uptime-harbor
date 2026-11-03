using System.Reflection;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Web.Controllers;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class InvitationSecurityTests
{
    [Fact]
    public void EveryMutationRequiresAdministratorAntiforgeryAndHasNoAnonymousSetupRoute()
    {
        // Arrange
        Type controller = typeof(ClusterInvitationsController);
        MethodInfo[] actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttribute<HttpPostAttribute>() is not null).ToArray();

        // Act
        AuthorizeAttribute authorization = controller.GetCustomAttribute<AuthorizeAttribute>()!;

        // Assert
        Assert.Equal("Administrator", authorization.Roles);
        Assert.Equal(4, actions.Length);
        Assert.All(actions, method => Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>()));
        Assert.Null(controller.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal("api/admin/cluster/invitations", controller.GetCustomAttribute<RouteAttribute>()!.Template);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualAntiforgeryFilterRejectsMissingTokenAndAcceptsBoundCookieAndHeader(bool includeToken)
    {
        // Arrange
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        services.AddControllersWithViews();
        using ServiceProvider provider = services.BuildServiceProvider();
        DefaultHttpContext http = new() { RequestServices = provider };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("first.invalid");
        http.Request.Method = "POST";
        if (includeToken)
        {
            IAntiforgery antiforgery = provider.GetRequiredService<IAntiforgery>();
            AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(http);
            string cookie = http.Response.Headers.SetCookie.ToString().Split(';')[0];
            http = new DefaultHttpContext { RequestServices = provider };
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("first.invalid");
            http.Request.Method = "POST";
            http.Request.Headers.Cookie = cookie;
            http.Request.Headers["X-CSRF-TOKEN"] = tokens.RequestToken;
        }

        ActionContext action = new(http, new RouteData(), new ActionDescriptor());
        AuthorizationFilterContext context = new(action, []);
        IFilterFactory factory = new ValidateAntiForgeryTokenAttribute();
        IAsyncAuthorizationFilter filter = Assert.IsAssignableFrom<IAsyncAuthorizationFilter>(factory.CreateInstance(provider));
        context.Filters.Add(filter);

        // Act
        await filter.OnAuthorizationAsync(context);

        // Assert
        if (includeToken)
        {
            Assert.Null(context.Result);
        }
        else
        {
            Assert.IsType<AntiforgeryValidationFailedResult>(context.Result);
        }
    }
}
