using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Web.Controllers;
using UptimeHarbor.Web.Controllers.Models;
using UptimeHarbor.Web.Pages;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.Issuers;

public class IssuerSecurityTests
{
    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("disabled", 403)]
    [InlineData("password", 403)]
    [InlineData("password-revision", 403)]
    [InlineData("pending-revision", 403)]
    [InlineData("barrier-revision", 403)]
    [InlineData("save-revision", 403)]
    [InlineData("expiry", 403)]
    [InlineData("schema", 409)]
    [InlineData("setup", 409)]
    [InlineData("paths", 409)]
    public async Task AuthorizationIsRepeatedBeforeEveryAuthorityBoundary(string mutation, int expected)
    {
        // Arrange
        using IssuerRegistrationFixture fixture = new();
        string password = "test-password";
        switch (mutation)
        {
            case "anonymous":
                fixture.CurrentUser.IsAuthenticated = false;
                break;
            case "disabled":
                RevisionImage image = fixture.State.Current!;
                fixture.State.Publish(image with { Values = image.Values.SetItem(fixture.Actor,
                    image.Values[fixture.Actor] with { Value = image.Values[fixture.Actor].Value with { Enabled = false } }) });
                break;
            case "password":
                password = "wrong";
                break;
            case "password-revision":
                fixture.Passwords.BeforeReturn = fixture.ChangeActor;
                break;
            case "pending-revision":
                fixture.Work.BeforeRead = fixture.ChangeActor;
                break;
            case "barrier-revision":
                fixture.Peer.BeforeResponse = fixture.ChangeActor;
                break;
            case "save-revision":
                fixture.Signing.AfterSave = fixture.ChangeActor;
                break;
            case "expiry":
                fixture.Signing.AfterSave = () => fixture.Time.Now = fixture.Time.Now.AddMinutes(6);
                break;
            case "schema":
                fixture.Options.SqlSchema = 2;
                break;
            case "setup":
                fixture.Local.InstallationIntent = "Setup";
                break;
            case "paths":
                fixture.Local.StateDirectory = string.Empty;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        // Act
        JsonResult result = await fixture.Controller.RegisterAsync(new(Guid.NewGuid(), password), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected, result.StatusCode);
        Assert.Empty(fixture.Work.Items);
        Assert.Empty(fixture.Projection.Writes);
        Assert.Empty(fixture.Audit.Entries);
        if (mutation is not ("save-revision" or "expiry"))
        {
            Assert.Empty(fixture.Signing.Issuers);
            Assert.False(Directory.Exists(fixture.Local.KeyDirectory));
        }
    }

    [Fact]
    public async Task TrustPendingCannotReplaceReauthenticatedActorRevisionWithNewAcceptedRevision()
    {
        // Arrange
        using IssuerRegistrationFixture fixture = new();
        fixture.Delivery.Offline = true;
        Guid id = Guid.NewGuid();
        await fixture.Controller.RegisterAsync(new(id, "test-password"), TestContext.Current.CancellationToken);
        RevisionCommand original = RevisionCodec.Decode(fixture.Payloads.Unprotect(fixture.Work.Items[id].ProtectedPayload));
        fixture.ChangeActor();

        // Act
        Exception? denied = await Record.ExceptionAsync(() => fixture.Pending.SubmitAsync(original, fixture.Options.NodeId, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(denied);
        Assert.Equal(original.ActorRevision, fixture.Work.Items[id].ActorUserRevision);
        Assert.Empty(fixture.Projection.Writes);
    }

    [Fact]
    public async Task RestoredPendingAndDifferentOperationCannotReplacePreparedIssuer()
    {
        // Arrange
        using IssuerRegistrationFixture fixture = new();
        fixture.Delivery.Offline = true;
        Guid id = Guid.NewGuid();
        await fixture.Controller.RegisterAsync(new(id, "test-password"), TestContext.Current.CancellationToken);
        fixture.Work.Items[id].RequireConfirmationAfterRestore();
        fixture.Delivery.Offline = false;

        // Act
        JsonResult held = await fixture.Controller.RegisterAsync(new(id, "test-password"), TestContext.Current.CancellationToken);
        JsonResult different = await fixture.Controller.RegisterAsync(new(Guid.NewGuid(), "test-password"), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(409, held.StatusCode);
        Assert.Equal(409, different.StatusCode);
        Assert.Single(fixture.Delivery.Sent);
        Assert.Single(fixture.Signing.Issuers);
        Assert.Empty(fixture.Projection.Writes);
    }

    [Fact]
    public async Task PublicSnapshotAndBrowserStatusNeverContainLocalKeyOrSecretReference()
    {
        // Arrange
        using IssuerRegistrationFixture fixture = new();
        Guid id = Guid.NewGuid();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddWebPresentation();
        using ServiceProvider provider = services.BuildServiceProvider();
        JsonSerializerOptions options = provider.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;

        // Act
        JsonResult response = await fixture.Controller.RegisterAsync(new(id, "test-password"), TestContext.Current.CancellationToken);
        string browser = JsonSerializer.Serialize(response.Value, options);
        string snapshot = JsonSerializer.Serialize(fixture.State.Current, options);
        string keyRef = fixture.Signing.Issuers[id].KeyReference;

        // Assert
        foreach (string secret in new[] { keyRef, "privateKey", "PRIVATE KEY", "keyReference", "wrapping.key", "test-password" })
        {
            Assert.DoesNotContain(secret, browser, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, snapshot, StringComparison.OrdinalIgnoreCase);
        }

        foreach (string internalField in new[] { "certificateDer", "spkiDigest", "certificateDigest", "payload", "securityStamp" })
        {
            Assert.DoesNotContain(internalField, browser, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("secret-password", new RegisterEnrollmentIssuerRequest(id, "secret-password").ToString());
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegisterEnrollmentIssuerRequest>(
            "{\"operationId\":\"" + id + "\",\"password\":\"x\",\"issuerEpoch\":\"" + id + "\"}", options));
    }

    [Fact]
    public void AdministrativeHttpEntryRequiresRoleAntiforgeryBoundsAndLimiter()
    {
        // Arrange
        Type controller = typeof(ClusterEnrollmentIssuersController);
        MethodInfo register = controller.GetMethod(nameof(ClusterEnrollmentIssuersController.RegisterAsync))!;

        // Act
        AuthorizeAttribute authorization = controller.GetCustomAttribute<AuthorizeAttribute>()!;
        CustomAttributeData bounds = controller.CustomAttributes.Single(item => item.AttributeType == typeof(RequestSizeLimitAttribute));

        // Assert
        Assert.Equal("Administrator", authorization.Roles);
        Assert.Equal(4096L, bounds.ConstructorArguments.Single().Value);
        Assert.NotNull(register.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.Equal("onboarding-administrator", register.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName);
        Assert.True(controller.GetCustomAttribute<ResponseCacheAttribute>()!.NoStore);
    }
}
