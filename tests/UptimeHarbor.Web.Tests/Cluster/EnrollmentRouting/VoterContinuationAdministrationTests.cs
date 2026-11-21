using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using UptimeHarbor.Web.Cluster.EnrollmentRouting;
using UptimeHarbor.Web.Controllers;
using UptimeHarbor.Web.Controllers.Models;
using UptimeHarbor.Web.Security;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class VoterContinuationAdministrationTests
{
    private const string HOST = "browser.invalid";
    private const string ORIGIN = "https://" + HOST;

    [Fact]
    public async Task ReadsMinimalStateAndReturnsSameAcceptedConfirmationOnRepeat()
    {
        // Arrange
        using VoterContinuationHttpAuthority fixture = new(administrator: true);
        await using ServiceProvider provider = Provider(fixture);
        using IServiceScope scope = provider.CreateScope();
        CommandResult proposal = await SubmitAsync(fixture, scope.ServiceProvider);
        ClusterVoterContinuationController controller = Controller(fixture, scope.ServiceProvider);
        CancellationToken token = TestContext.Current.CancellationToken;

        // Act
        JsonResult before = await controller.ReadAsync(fixture.RequestId, token);

        // Assert
        Assert.Equal(200, before.StatusCode);
        JsonElement status = Data(before);
        Assert.Equal(proposal.Digest, status.GetProperty("RequestDigest").GetString());
        Assert.Equal(fixture.VoterId, status.GetProperty("NodeId").GetGuid());
        Assert.Equal(fixture.RequestId, status.GetProperty("RequestId").GetGuid());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("ConfirmationId").ValueKind);
        Assert.Equal(4, status.EnumerateObject().Count());
        Assert.Equal(0, fixture.PasswordChecks);

        // Act
        controller.Request.Method = "POST";
        ConfirmVoterContinuationRequest request = new(proposal.Digest, VoterContinuationHttpAuthority.PASSWORD);
        JsonResult first = await controller.ConfirmAsync(fixture.RequestId, request, token);
        JsonResult replay = await controller.ConfirmAsync(fixture.RequestId, request, token);
        controller.Request.Method = "GET";
        JsonResult after = await controller.ReadAsync(fixture.RequestId, token);

        // Assert
        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, replay.StatusCode);
        Assert.Equal(200, after.StatusCode);
        Guid accepted = Data(first).GetGuid();
        Assert.NotEqual(Guid.Empty, accepted);
        Assert.Equal(accepted, Data(replay).GetGuid());
        Assert.Equal(accepted, Data(after).GetProperty("ConfirmationId").GetGuid());
        Assert.Equal(2, fixture.Writes);
        Assert.Equal(2, fixture.PasswordChecks);
        RecordingLog audit = provider.GetRequiredService<RecordingLog>();
        Assert.Equal(2, audit.Events.Count);
        Assert.All(audit.Events, entry =>
        {
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Contains(fixture.ActorId.ToString(), entry.Message);
            Assert.Contains(accepted.ToString(), entry.Message);
            Assert.DoesNotContain(VoterContinuationHttpAuthority.PASSWORD, entry.Message);
            Assert.DoesNotContain(proposal.Digest, entry.Message);
        });
    }

    [Fact]
    public async Task DigestForAnotherAcceptedRequestCannotAuthorizeSelectedRequest()
    {
        // Arrange
        using VoterContinuationHttpAuthority fixture = new(administrator: true);
        await using ServiceProvider provider = Provider(fixture);
        using IServiceScope scope = provider.CreateScope();
        await SubmitAsync(fixture, scope.ServiceProvider);
        CommandResult other = await SubmitAsync(fixture, scope.ServiceProvider, alternate: true);
        ClusterVoterContinuationController controller = Controller(fixture, scope.ServiceProvider);
        controller.Request.Method = "POST";

        // Act
        JsonResult response = await controller.ConfirmAsync(fixture.RequestId,
            new(other.Digest, VoterContinuationHttpAuthority.PASSWORD), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(409, response.StatusCode);
        Assert.Equal(2, fixture.Writes);
        Assert.Equal(0, fixture.PasswordChecks);
    }

    [Theory]
    [InlineData("password", 400, 1)]
    [InlineData("digest", 409, 0)]
    [InlineData("request", 409, 0)]
    [InlineData("setup", 403, 0)]
    [InlineData("closed", 403, 0)]
    [InlineData("principal-only", 403, 0)]
    [InlineData("role", 403, 0)]
    [InlineData("disabled", 409, 0)]
    [InlineData("revision", 409, 1)]
    [InlineData("majority", 409, 0)]
    [InlineData("accepted", 409, 0)]
    [InlineData("origin", 403, 0)]
    [InlineData("no-origin", 403, 0)]
    [InlineData("host", 403, 0)]
    public async Task RefusalCannotAcceptConfirmation(string failure, int expected, int checks)
    {
        // Arrange
        using VoterContinuationHttpAuthority fixture = new(administrator: true);
        await using ServiceProvider provider = Provider(fixture);
        using IServiceScope scope = provider.CreateScope();
        CommandResult proposal = await SubmitAsync(fixture, scope.ServiceProvider);
        ClusterVoterContinuationController controller = Controller(fixture, scope.ServiceProvider, failure);
        controller.Request.Method = "POST";
        Guid requestId = fixture.RequestId;
        string digest = proposal.Digest;
        string password = VoterContinuationHttpAuthority.PASSWORD;
        switch (failure)
        {
            case "closed":
                InstallationState installation = provider.GetRequiredService<InstallationState>();
                installation.Publish(fixture.Profile with
                {
                    Phase = InstallationPhase.SetupReady,
                    ProfileRevision = fixture.Profile.ProfileRevision + 1
                });
                break;
            case "password":
                password = "wrong";
                break;
            case "digest":
                digest = new string('A', 64);
                break;
            case "request":
                requestId = Guid.NewGuid();
                break;
            case "disabled":
                fixture.ChangeAccount(enabled: false);
                break;
            case "revision":
                fixture.AfterPasswordCheck = () => fixture.ChangeAccount(enabled: true);
                break;
            case "majority":
                fixture.Majority = false;
                break;
            case "accepted":
                fixture.AcceptedAuthority = false;
                break;
            case "origin":
                controller.Request.Headers.Origin = "https://other.invalid";
                break;
            case "no-origin":
                controller.Request.Headers.Remove("Origin");
                break;
            case "host":
                controller.Request.Host = new("other.invalid");
                break;
        }

        // Act
        JsonResult response = await controller.ConfirmAsync(requestId, new(digest, password), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(1, fixture.Writes);
        Assert.Equal(checks, fixture.PasswordChecks);
        string json = JsonSerializer.Serialize(response.Value);
        Assert.DoesNotContain("example-administrator-password", json);
        Assert.DoesNotContain(proposal.Digest, json);
        RecordingLog audit = provider.GetRequiredService<RecordingLog>();
        Assert.DoesNotContain(audit.Events, entry => entry.Level == LogLevel.Information);
        if (failure == "password")
        {
            var entry = Assert.Single(audit.Events);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Contains(fixture.ActorId.ToString(), entry.Message);
            Assert.DoesNotContain(password, entry.Message);
        }
    }

    [Theory]
    [InlineData("disabled", 409)]
    [InlineData("majority", 409)]
    [InlineData("accepted", 409)]
    [InlineData("setup", 403)]
    [InlineData("origin", 403)]
    public async Task ReadDoesNotRevealRequestWithoutTrustedAcceptedAccess(string failure, int expected)
    {
        // Arrange
        using VoterContinuationHttpAuthority fixture = new(administrator: true);
        await using ServiceProvider provider = Provider(fixture);
        using IServiceScope scope = provider.CreateScope();
        CommandResult proposal = await SubmitAsync(fixture, scope.ServiceProvider);
        ClusterVoterContinuationController controller = Controller(fixture, scope.ServiceProvider, failure);
        if (failure == "disabled")
        {
            fixture.ChangeAccount(enabled: false);
        }

        fixture.Majority = failure != "majority";
        fixture.AcceptedAuthority = failure != "accepted";
        if (failure == "origin")
        {
            controller.Request.Headers.Origin = "https://other.invalid";
        }

        // Act
        JsonResult response = await controller.ReadAsync(fixture.RequestId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain(proposal.Digest, JsonSerializer.Serialize(response.Value));
        Assert.Equal(1, fixture.Writes);
        Assert.Equal(0, fixture.PasswordChecks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualPostFilterRequiresBoundAntiforgeryCookieAndHeader(bool includeToken)
    {
        // Arrange
        using VoterContinuationHttpAuthority fixture = new(administrator: true);
        await using ServiceProvider provider = Provider(fixture);
        using IServiceScope scope = provider.CreateScope();
        ClusterVoterContinuationController controller = Controller(fixture, scope.ServiceProvider);
        Type type = typeof(ClusterVoterContinuationController);
        MethodInfo action = type.GetMethod(nameof(ClusterVoterContinuationController.ConfirmAsync))!;
        Assert.Equal(ApplicationRoles.ADMINISTRATOR, type.GetCustomAttribute<AuthorizeAttribute>()!.Roles);
        Assert.True(type.GetCustomAttribute<ResponseCacheAttribute>()!.NoStore);
        Assert.NotNull(action.GetCustomAttribute<HttpPostAttribute>());
        ValidateAntiForgeryTokenAttribute attribute = action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>()!;
        IAsyncAuthorizationFilter filter = Assert.IsAssignableFrom<IAsyncAuthorizationFilter>(attribute.CreateInstance(scope.ServiceProvider));
        HttpContext http = controller.HttpContext;
        if (includeToken)
        {
            AntiforgeryTokenSet tokens = scope.ServiceProvider.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
            controller = Controller(fixture, scope.ServiceProvider);
            controller.Request.Headers.Cookie = http.Response.Headers.SetCookie.ToString().Split(';')[0];
            controller.Request.Headers["RequestVerificationToken"] = tokens.RequestToken;
        }

        controller.Request.Method = "POST";
        AuthorizationFilterContext context = new(new ActionContext(controller.HttpContext, new RouteData(), new ActionDescriptor()), [filter]);

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

    private static ServiceProvider Provider(VoterContinuationHttpAuthority fixture)
    {
        InstallationState installation = new();
        installation.Publish(fixture.Profile);
        installation.ConfirmCommonOpen(fixture.Profile);
        ServiceCollection services = new();
        services.AddSingleton(installation);
        services.AddHttpContextAccessor();
        services.AddLogging();
        services.AddSingleton<RecordingLog>();
        services.AddSingleton<ILogger<ClusterVoterContinuationController>>(provider => provider.GetRequiredService<RecordingLog>());
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
        services.AddControllersWithViews();
        services.AddSingleton<EnrollmentSigner>(_ => throw new InvalidOperationException("Signing forbidden"));
        services.AddSingleton<LocalVoterCredentialPublication>(_ => throw new InvalidOperationException("Publication forbidden"));
        fixture.AddServices(services);
        services.AddEnrollmentPeerRouting();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task<CommandResult> SubmitAsync(VoterContinuationHttpAuthority fixture, IServiceProvider services, bool alternate = false)
    {
        VoterContinuationAuthority authority = services.GetRequiredService<VoterContinuationAuthority>();
        CancellationToken token = TestContext.Current.CancellationToken;
        VoterCredentialProofRequest challenge = fixture.Sign(VoterContinuationHttpAuthority.RandomChallenge(), VoterContinuationAuthority.REQUEST, alternate);
        string nonce = await authority.IssueChallengeAsync(challenge, VoterContinuationAuthority.REQUEST, null, token);
        CommandResult result = await authority.RequestAsync(fixture.Sign(nonce, alternate: alternate), token);
        Assert.Equal(OperationOutcome.Committed, result.Outcome);
        return result;
    }

    private static ClusterVoterContinuationController Controller(VoterContinuationHttpAuthority fixture, IServiceProvider services,
        string failure = "")
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, fixture.ActorId.ToString())];
        if (failure != "role")
        {
            claims.Add(new(ClaimTypes.Role, ApplicationRoles.ADMINISTRATOR));
        }

        string scheme = IdentityConstants.ApplicationScheme;
        if (failure == "setup")
        {
            scheme = SetupAuthentication.SCHEME;
        }

        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, scheme));
        DefaultHttpContext http = new() { RequestServices = services, User = principal };
        http.Request.Host = new(HOST);
        http.Request.Scheme = "https";
        http.Request.Method = "GET";
        http.Request.Headers.Origin = ORIGIN;
        if (failure != "principal-only")
        {
            AuthenticationTicket ticket = new(principal, scheme);
            http.Features.Set<IAuthenticateResultFeature>(new TicketFeature { AuthenticateResult = AuthenticateResult.Success(ticket) });
        }

        services.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
        return new(Options.Create(new InstallationOptions { BrowserOrigin = ORIGIN }), services.GetRequiredService<ILogger<ClusterVoterContinuationController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static JsonElement Data(JsonResult response)
    {
        return JsonSerializer.SerializeToElement(response.Value).GetProperty("data");
    }

    private sealed class TicketFeature : IAuthenticateResultFeature
    {
        public AuthenticateResult? AuthenticateResult { get; set; }
    }

    private sealed class RecordingLog : ILogger<ClusterVoterContinuationController>
    {
        internal List<(LogLevel Level, string Message)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Events.Add((logLevel, formatter(state, exception)));
        }
    }
}
