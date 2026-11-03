using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Web.Controllers.Models;
using UptimeHarbor.Web.Pages;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class InvitationHttpTests
{
    [Fact]
    public async Task FirstAcceptedResponseShowsCodeOnceAndRetryNeverCreatesAnotherInvitation()
    {
        // Arrange
        InvitationHttpFixture fixture = new();
        IssueInvitationRequest request = new(Guid.NewGuid(), null, "test-password");

        // Act
        JsonResult first = await fixture.Controller.IssueAsync(request, TestContext.Current.CancellationToken);
        JsonResult retry = await fixture.Controller.IssueAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(200, first.StatusCode);
        string code = JsonSerializer.SerializeToElement(first.Value).GetProperty("data").GetProperty("Code").GetString()!;
        Assert.Equal(4, Guid.Parse(code).Version);
        Assert.Null(JsonSerializer.SerializeToElement(retry.Value).GetProperty("data").GetProperty("Code").GetString());
        Assert.Single(fixture.State.Current!.Onboarding!.Invitations);
        Assert.Single(fixture.Work.Items);
        Assert.Equal(2, fixture.Work.Items[request.OperationId].ProtocolVersion);
        Assert.Equal(2, fixture.Work.Items[request.OperationId].SchemaVersion);
        Assert.Single(fixture.Store.Writes);
        Assert.Single(fixture.AuditLog.Messages, message => message.Contains("InvitationIssued", StringComparison.Ordinal));
        string logs = string.Join("\n", fixture.AuditLog.Messages);
        Assert.DoesNotContain(code, logs);
        Assert.DoesNotContain(fixture.State.Current.Onboarding.Invitations[request.OperationId].Verifier, logs);
        Assert.Contains(fixture.Actor.ToString(), logs);
    }

    [Fact]
    public async Task LostResponseAndNewLeaderWithNoLocalPendingReturnStateWithoutPlaintext()
    {
        // Arrange
        InvitationHttpFixture fixture = new();
        fixture.Delivery.DropResponse = true;
        IssueInvitationRequest request = new(Guid.NewGuid(), null, "test-password");

        // Act
        JsonResult lost = await fixture.Controller.IssueAsync(request, TestContext.Current.CancellationToken);
        fixture.Work.Items.Clear();
        JsonResult retry = await fixture.Controller.IssueAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(200, lost.StatusCode);
        Assert.Equal(200, retry.StatusCode);
        Assert.Null(JsonSerializer.SerializeToElement(lost.Value).GetProperty("data").GetProperty("Code").GetString());
        Assert.Null(JsonSerializer.SerializeToElement(retry.Value).GetProperty("data").GetProperty("Code").GetString());
        Assert.Single(fixture.State.Current!.Onboarding!.Invitations);
        Assert.Single(fixture.Store.Writes);
        Assert.Equal(1, fixture.Delivery.Sends);
    }

    [Fact]
    public async Task PendingResponseContainsNoCodeAndLaterAcceptanceCannotRestoreIt()
    {
        // Arrange
        InvitationHttpFixture fixture = new();
        fixture.Delivery.NoLeader = true;
        IssueInvitationRequest request = new(Guid.NewGuid(), null, "test-password");

        // Act
        JsonResult pending = await fixture.Controller.IssueAsync(request, TestContext.Current.CancellationToken);
        fixture.Delivery.NoLeader = false;
        JsonResult later = await fixture.Controller.IssueAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(202, pending.StatusCode);
        Assert.Equal(200, later.StatusCode);
        Assert.Null(JsonSerializer.SerializeToElement(pending.Value).GetProperty("data").GetProperty("Code").GetString());
        Assert.Null(JsonSerializer.SerializeToElement(later.Value).GetProperty("data").GetProperty("Code").GetString());
        Assert.Single(fixture.Work.Items);
        Assert.Single(fixture.State.Current!.Onboarding!.Invitations);
    }

    [Theory]
    [InlineData(false, true, "test-password", 401)]
    [InlineData(true, false, "test-password", 403)]
    [InlineData(true, true, "wrong", 403)]
    public async Task AuthorizationAndReauthenticationFailBeforeIssuance(bool authenticated, bool enabled, string password, int expected)
    {
        // Arrange
        InvitationHttpFixture fixture = new(authenticated, enabled);

        // Act
        JsonResult result = await fixture.Controller.IssueAsync(new(Guid.NewGuid(), null, password), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected, result.StatusCode);
        Assert.Empty(fixture.Work.Items);
        Assert.Empty(fixture.Store.Writes);
        Assert.DoesNotContain(fixture.AuditLog.Messages, message => message.Contains("InvitationIssued", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CredentialRevisionChangingDuringPasswordOrPendingReadDeniesIssuance(bool duringPassword)
    {
        // Arrange
        InvitationHttpFixture fixture = new();
        if (duringPassword)
        {
            fixture.Passwords.BeforeReturn = fixture.ChangeActor;
        }
        else
        {
            fixture.Work.BeforeRead = fixture.ChangeActor;
        }

        // Act
        JsonResult result = await fixture.Controller.IssueAsync(new(Guid.NewGuid(), null, "test-password"), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(403, result.StatusCode);
        Assert.Empty(fixture.Work.Items);
        Assert.Empty(fixture.Store.Writes);
    }

    [Fact]
    public async Task FailedProjectionCannotPublishSuccessOrWriteIssuedLog()
    {
        // Arrange
        InvitationHttpFixture fixture = new();
        fixture.Store.Fail = true;

        // Act
        Exception? failure = await Record.ExceptionAsync(() => fixture.Controller.IssueAsync(
            new(Guid.NewGuid(), null, "test-password"), TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
        Assert.False(fixture.State.Available);
        Assert.Null(fixture.State.Current!.Onboarding);
        Assert.Empty(fixture.AuditLog.Messages);
    }

    [Fact]
    public async Task ExplicitReplaceRevokesOldCodeAndChangedRetryCannotAlterPayload()
    {
        // Arrange
        InvitationHttpFixture fixture = new();
        Guid firstId = Guid.NewGuid();
        await fixture.Controller.IssueAsync(new(firstId, null, "test-password"), TestContext.Current.CancellationToken);
        Guid secondId = Guid.NewGuid();

        // Act
        JsonResult replaced = await fixture.Controller.IssueAsync(new(secondId, firstId, "test-password"), TestContext.Current.CancellationToken);
        JsonResult changed = await fixture.Controller.IssueAsync(new(secondId, null, "test-password"), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(200, replaced.StatusCode);
        Assert.Equal(409, changed.StatusCode);
        Assert.Equal(InvitationStatus.Revoked, fixture.State.Current!.Onboarding!.Invitations[firstId].Status);
        Assert.Single(fixture.State.Current.Onboarding.Invitations.Values, item => item.Status == InvitationStatus.Unused);
    }

    [Fact]
    public void SafeDtoUsesActualMvcOptionsAndDoesNotExposeInternalPayloadOrCredentialFields()
    {
        // Arrange
        ServiceCollection services = new();
        services.AddLogging();
        services.AddWebPresentation();
        using ServiceProvider provider = services.BuildServiceProvider();
        JsonSerializerOptions options = provider.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        InvitationResponse response = new(Guid.NewGuid(), Guid.NewGuid(), "Committed", "Reserved", DateTimeOffset.UnixEpoch,
            Guid.NewGuid(), "Reserved", null);

        // Act
        string json = JsonSerializer.Serialize(response, options);

        // Assert
        foreach (string secret in new[] { "verifier", "credential", "payload", "spki", "privateKey", "password" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IssueInvitationRequest>(
            "{\"operationId\":\"" + Guid.NewGuid() + "\",\"password\":\"x\",\"actorId\":\"" + Guid.NewGuid() + "\"}", options));
    }

    [Fact]
    public async Task RestoredPendingCannotBeSentUntilExplicitReconciliation()
    {
        // Arrange
        InvitationHttpFixture fixture = new();
        fixture.Delivery.NoLeader = true;
        IssueInvitationRequest request = new(Guid.NewGuid(), null, "test-password");
        await fixture.Controller.IssueAsync(request, TestContext.Current.CancellationToken);
        fixture.Work.Items[request.OperationId].RequireConfirmationAfterRestore();
        fixture.Delivery.NoLeader = false;

        // Act
        JsonResult result = await fixture.Controller.IssueAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(409, result.StatusCode);
        Assert.Equal(1, fixture.Delivery.Sends);
        Assert.Null(fixture.State.Current!.Onboarding);
        Assert.Empty(fixture.Store.Writes);
    }

    [Fact]
    public async Task UnknownOperationIsUnconfirmedWithoutInventingLocalPending()
    {
        // Arrange
        InvitationHttpFixture fixture = new();

        // Act
        JsonResult result = await fixture.Controller.ReadAsync(Guid.NewGuid(), Guid.NewGuid(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("Unconfirmed", JsonSerializer.SerializeToElement(result.Value).GetProperty("data").GetProperty("OperationStatus").GetString());
        Assert.Empty(fixture.Work.Items);
        Assert.Empty(fixture.AuditLog.Messages);
    }
}
