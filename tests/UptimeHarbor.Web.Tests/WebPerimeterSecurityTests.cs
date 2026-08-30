using System.Reflection;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Web.Controllers;
using UptimeHarbor.Web.Pages.Account;
using UptimeHarbor.Web.Security;
using Xunit;

namespace UptimeHarbor.Tests.Web;

public class WebPerimeterSecurityTests
{
    [Fact]
    public void LoginPage_HasLoginSpecificRateLimitPolicy()
    {
        EnableRateLimitingAttribute attribute = Assert.Single(
            typeof(LoginModel).GetCustomAttributes<EnableRateLimitingAttribute>());

        Assert.Equal(WebSecurityPolicies.LOGIN_RATE_LIMIT_POLICY, attribute.PolicyName);
    }

    [Theory]
    [InlineData(typeof(DashboardController))]
    [InlineData(typeof(AdminObjectsController))]
    [InlineData(typeof(AdminUsersController))]
    [InlineData(typeof(AdminNotificationsController))]
    public void MainApis_DoNotHaveLoginSpecificRateLimitPolicy(Type controllerType)
    {
        Assert.Empty(controllerType.GetCustomAttributes<EnableRateLimitingAttribute>());
    }

    [Fact]
    public async Task RateLimitRejection_ReturnsSafeLoginAgnosticResponse()
    {
        ServiceCollection services = new();
        UptimeHarbor.Web.Pages.ServiceCollectionExtensions.AddWebPresentation(services);
        using ServiceProvider provider = services.BuildServiceProvider();
        RateLimiterOptions options = provider
            .GetRequiredService<IOptions<RateLimiterOptions>>()
            .Value;
        DefaultHttpContext httpContext = new();
        await using MemoryStream responseBody = new();
        httpContext.Response.Body = responseBody;
        OnRejectedContext rejectedContext = new()
        {
            HttpContext = httpContext,
            Lease = new RejectedLease()
        };

        Assert.NotNull(options.OnRejected);
        await options.OnRejected!(rejectedContext, TestContext.Current.CancellationToken);
        responseBody.Position = 0;
        using StreamReader reader = new(responseBody);
        string response = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Equal("text/plain; charset=utf-8", httpContext.Response.ContentType);
        Assert.Equal("Слишком много попыток входа. Повторите позже.", response);
        Assert.DoesNotContain("логин", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("пользователь", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecurityHeaders_AreAddedWithSafeValues()
    {
        ServiceCollection services = new();
        using ServiceProvider provider = services.BuildServiceProvider();
        ApplicationBuilder app = new(provider);
        app.UseSecurityHeaders();
        app.Run(context => context.Response.WriteAsync("ok"));
        RequestDelegate pipeline = app.Build();
        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();

        await pipeline(context);

        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"]);
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"]);

        string contentSecurityPolicy = context.Response.Headers["Content-Security-Policy"].ToString();
        Assert.Contains("default-src 'self'", contentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", contentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("style-src 'self'", contentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("img-src 'self'", contentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("font-src 'self'", contentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", contentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", contentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("base-uri 'self'", contentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", contentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("form-action 'self'", contentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", contentSecurityPolicy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unsafe-eval", contentSecurityPolicy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("*", contentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.", contentSecurityPolicy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IndexPage_UsesLocalVersionedSignalRAsset()
    {
        string page = File.ReadAllText(GetRepositoryPath("UptimeHarbor.Web", "Pages", "Index.cshtml"));

        Assert.DoesNotContain("cdn.jsdelivr.net", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("~/vendor/signalr/signalr.min.js", page, StringComparison.Ordinal);
        Assert.Contains("asp-append-version=\"true\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalSignalRAsset_IsPresentAndPinnedToProvided10_0_11()
    {
        string assetPath = GetRepositoryPath("UptimeHarbor.Web", "wwwroot", "vendor", "signalr", "signalr.min.js");
        Assert.True(File.Exists(assetPath));

        string asset = File.ReadAllText(assetPath);
        Assert.Contains("const p=\"10.0.11\"", asset, StringComparison.Ordinal);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(assetPath));
        Assert.Equal(
            "97E9B97E642A72E5A470917147A2BF79F86CAD829A5C6786ADB10614D248BB95",
            Convert.ToHexString(hash));
    }

    private static string GetRepositoryPath(params string[] segments)
    {
        return Path.GetFullPath(Path.Combine(
            [
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                .. segments
            ]));
    }

    private class RejectedLease : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
