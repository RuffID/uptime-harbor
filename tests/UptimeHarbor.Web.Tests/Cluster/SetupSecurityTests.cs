using System.Reflection;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using UptimeHarbor.Infrastructure.Telegram;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Web.Configuration;
using UptimeHarbor.Web.Controllers;
using UptimeHarbor.Web.Security;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class SetupSecurityTests
{
    [Theory]
    [InlineData("Setup", "Disabled")]
    [InlineData("Legacy", "Bootstrap")]
    [InlineData("Legacy", "Join")]
    [InlineData("Legacy", "Restart")]
    public void NonsenderStartupDoesNotRequireTelegramToken(string intent, string mode)
    {
        // Arrange
        ServiceCollection services = new();
        services.AddApplicationConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Installation:InstallationIntent"] = intent, ["Cluster:Mode"] = mode
        }).Build());
        using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        TelegramOptions options = provider.GetRequiredService<IOptions<TelegramOptions>>().Value;

        // Assert
        Assert.False(options.HasValidBotToken());
    }

    [Fact]
    public async Task LegacySharedAssetsRemainAccessibleWhileSetupRoutesAreClosed()
    {
        // Arrange
        RuntimeWriteGate gate = new();
        gate.Open(1);
        SetupBoundaryMiddleware middleware = new(http => { http.Response.StatusCode = 202; return Task.CompletedTask; });
        DefaultHttpContext shared = new();
        shared.Request.Path = "/lib/bootstrap/dist/css/bootstrap.min.css";
        DefaultHttpContext setup = new();
        setup.Request.Path = "/api/setup/login";

        // Act
        await middleware.InvokeAsync(shared, Options.Create(new InstallationOptions()), new InstallationState(), gate);
        await middleware.InvokeAsync(setup, Options.Create(new InstallationOptions()), new InstallationState(), gate);

        // Assert
        Assert.Equal(202, shared.Response.StatusCode);
        Assert.Equal(404, setup.Response.StatusCode);
    }

    [Fact]
    public async Task LoginIdentityChangeRequiresNewAntiforgeryToken()
    {
        // Arrange
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
        using ServiceProvider provider = services.BuildServiceProvider();
        IAntiforgery antiforgery = provider.GetRequiredService<IAntiforgery>();
        DefaultHttpContext anonymous = new() { RequestServices = provider };
        anonymous.Request.Scheme = "https";
        AntiforgeryTokenSet before = antiforgery.GetAndStoreTokens(anonymous);
        string cookie = anonymous.Response.Headers.SetCookie.ToString().Split(';')[0];
        DefaultHttpContext authenticated = new() { RequestServices = provider };
        authenticated.Request.Scheme = "https";
        authenticated.User = SetupAuthentication.Principal(new(Guid.NewGuid(), Guid.NewGuid(), "stamp", 1,
            Guid.NewGuid(), DateTimeOffset.UtcNow));
        authenticated.Request.Headers.Cookie = cookie;
        AntiforgeryTokenSet after = antiforgery.GetAndStoreTokens(authenticated);
        DefaultHttpContext post = new() { RequestServices = provider, User = authenticated.User };
        post.Request.Scheme = "https";
        post.Request.Method = "POST";
        post.Request.Headers.Cookie = cookie;
        post.Request.Headers["RequestVerificationToken"] = before.RequestToken;

        // Act
        bool oldToken = await antiforgery.IsRequestValidAsync(post);
        DefaultHttpContext freshPost = new() { RequestServices = provider, User = authenticated.User };
        freshPost.Request.Scheme = "https";
        freshPost.Request.Method = "POST";
        freshPost.Request.Headers.Cookie = cookie;
        freshPost.Request.Headers["RequestVerificationToken"] = after.RequestToken;
        bool freshToken = await antiforgery.IsRequestValidAsync(freshPost);

        // Assert
        Assert.False(oldToken);
        Assert.True(freshToken);
    }

    [Fact]
    public void SetupAndCommonTicketsCannotCrossSchemesOrInstallIdentities()
    {
        // Arrange
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAuthentication().AddCookie(IdentityConstants.ApplicationScheme);
        services.AddSetupAuthentication(Guid.NewGuid());
        using ServiceProvider provider = services.BuildServiceProvider();
        IOptionsMonitor<CookieAuthenticationOptions> options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        CookieAuthenticationOptions setup = options.Get(SetupAuthentication.SCHEME);
        CookieAuthenticationOptions common = options.Get(IdentityConstants.ApplicationScheme);
        SetupSession session = new(Guid.NewGuid(), Guid.NewGuid(), "stamp", 1, Guid.NewGuid(), DateTimeOffset.UtcNow);
        AuthenticationTicket ticket = new(SetupAuthentication.Principal(session), SetupAuthentication.SCHEME);

        // Act
        string encoded = setup.TicketDataFormat.Protect(ticket);
        AuthenticationTicket? crossed = common.TicketDataFormat.Unprotect(encoded);
        string commonCookie = common.TicketDataFormat.Protect(new AuthenticationTicket(new(new System.Security.Claims.ClaimsIdentity(
            [new(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], IdentityConstants.ApplicationScheme)), IdentityConstants.ApplicationScheme));
        AuthenticationTicket? reversed = setup.TicketDataFormat.Unprotect(commonCookie);

        // Assert
        Assert.Null(crossed);
        Assert.Null(reversed);
        Assert.NotNull(setup.TicketDataFormat.Unprotect(encoded));
        TicketDataFormat foreignInstall = new(provider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector($"LocalSetup/{Guid.NewGuid():N}", "Cookie/v1"));
        Assert.Null(foreignInstall.Unprotect(encoded));
        Assert.Null(SetupAuthentication.Read(new(new System.Security.Claims.ClaimsIdentity(IdentityConstants.ApplicationScheme))));
        Assert.Equal("__Host-UptimeHarbor.Setup", setup.Cookie.Name);
        Assert.Equal(CookieSecurePolicy.Always, setup.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Strict, setup.Cookie.SameSite);
        Assert.True(setup.Cookie.HttpOnly);
        Assert.Equal(TimeSpan.FromMinutes(15), setup.ExpireTimeSpan);
    }

    [Theory]
    [InlineData("https://browser.example", "browser.example", 200)]
    [InlineData("https://attacker.example", "browser.example", 403)]
    [InlineData("", "browser.example", 403)]
    [InlineData("https://browser.example", "attacker.example", 403)]
    public async Task EveryMutationRequiresExactOriginAndHost(string origin, string host, int expected)
    {
        // Arrange
        DefaultHttpContext context = new();
        context.Request.Path = "/api/setup/prepare";
        context.Request.Scheme = "https";
        context.Request.Host = new(host);
        context.Request.Method = "POST";
        context.Request.Headers.Origin = origin;
        SetupBoundaryMiddleware middleware = new(http => Task.CompletedTask);

        // Act
        await middleware.InvokeAsync(context, Options.Create(new InstallationOptions
        {
            InstallationIntent = "Setup", BrowserOrigin = "https://browser.example"
        }), new InstallationState(), new RuntimeWriteGate());

        // Assert
        Assert.Equal(expected, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.False(SetupBoundaryMiddleware.Allowed("/api/admin/users"));
        Assert.False(SetupBoundaryMiddleware.Allowed("/api/setup/prepare/extra"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualAntiforgeryValidationRejectsMissingToken(bool includeToken)
    {
        // Arrange
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
        services.AddControllersWithViews();
        using ServiceProvider provider = services.BuildServiceProvider();
        DefaultHttpContext context = new() { RequestServices = provider };
        context.Request.Scheme = "https";
        context.Request.Host = new("browser.example");
        AntiforgeryTokenSet tokens = provider.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
        string cookie = context.Response.Headers.SetCookie.ToString().Split(';')[0];
        DefaultHttpContext post = new() { RequestServices = provider };
        post.Request.Scheme = "https";
        post.Request.Host = new("browser.example");
        post.Request.Method = "POST";
        post.Request.Headers.Cookie = cookie;
        if (includeToken)
        {
            post.Request.Headers["RequestVerificationToken"] = tokens.RequestToken;
        }

        AuthorizationFilterContext filterContext = new(new ActionContext(post, new RouteData(), new ActionDescriptor()), []);
        IFilterFactory factory = new ValidateAntiForgeryTokenAttribute();
        IAsyncAuthorizationFilter filter = (IAsyncAuthorizationFilter)factory.CreateInstance(provider);
        filterContext.Filters.Add(filter);

        // Act
        await filter.OnAuthorizationAsync(filterContext);

        // Assert
        if (includeToken)
        {
            Assert.Null(filterContext.Result);
        }
        else
        {
            Assert.IsType<AntiforgeryValidationFailedResult>(filterContext.Result);
        }
    }

    [Fact]
    public void SetupRoutesRequireOwnSchemeAndEveryPostHasAntiforgery()
    {
        // Arrange
        Type controller = typeof(SetupController);
        MethodInfo[] mutations = controller.GetMethods().Where(method => method.GetCustomAttribute<HttpPostAttribute>() is not null).ToArray();

        // Act
        AuthorizeAttribute authorization = controller.GetCustomAttribute<AuthorizeAttribute>()!;

        // Assert
        Assert.Equal(SetupAuthentication.SCHEME, authorization.AuthenticationSchemes);
        Assert.Equal(3, mutations.Length);
        Assert.All(mutations, method => Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>()));
        Assert.Null(controller.GetMethod(nameof(SetupController.PrepareAsync))!.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(controller.GetCustomAttribute<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>());
        Assert.True(controller.GetCustomAttribute<ResponseCacheAttribute>()!.NoStore);
    }
}
