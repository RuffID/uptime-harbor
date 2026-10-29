using System.Text;
using System.Text.Json;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Application.Users.Models;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Users;

public class SharedUserManagementTests
{
    [Fact]
    public async Task CreateAndLostResponseRetryRetainPreparedCredentialAndOriginalResult()
    {
        // Arrange
        SharedAccountFixture fixture = new();
        AccountChangeRequest request = new(Guid.NewGuid(), Guid.NewGuid(), 0, "Create", "new-admin", "TopSecret-123!");

        // Act
        ServiceResult<AccountOperationDto> first = await fixture.Service.SubmitAsync(request, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        ServiceResult<AccountOperationDto> pendingRetry = await fixture.Create(fixture.Scenario.Actor)
            .SubmitAsync(request, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Pending", first.Data!.Status);
        Assert.Equal(first.Data, pendingRetry.Data);
        Assert.Null(fixture.State.Find(request.UserId));
        Assert.Equal(1, fixture.Passwords.Preparations);
        Assert.Single(fixture.Work.Items);
        string payload = Encoding.UTF8.GetString(fixture.Payloads.Unprotect(fixture.Work.Items[request.OperationId].ProtectedPayload));
        Assert.DoesNotContain(request.Password!, payload);
        Assert.Equal(fixture.Scenario.Role, fixture.Command(request.OperationId).Value!.RoleId);
        Assert.Equal(["LocalPending"], fixture.Events.Outcomes);

        // Act
        CommandResult accepted = fixture.Commit(request.OperationId);
        ServiceResult<AccountOperationDto> retry = await fixture.Service.SubmitAsync(request, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(OperationOutcome.Committed, accepted.Outcome);
        Assert.Equal("Committed", retry.Data!.Status);
        Assert.Equal(accepted.AppliedRevision, retry.Data.Revision);
        Assert.NotNull(fixture.State.Find(request.UserId));
        Assert.Equal(1, fixture.Passwords.Preparations);
        Assert.Single(fixture.Events.Outcomes);
    }

    [Fact]
    public async Task UpdatePasswordDisableEnableAndDeleteUseAcceptedVersionsAndTombstone()
    {
        // Arrange
        SharedAccountFixture fixture = new();
        Guid target = fixture.Scenario.OtherActor;
        string originalHash = fixture.State.Current!.Values[target].Value.PasswordHash!;
        AccountChangeRequest update = new(Guid.NewGuid(), target, 2, "Update", "renamed");

        // Act
        await fixture.Service.SubmitAsync(update, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        fixture.Commit(update.OperationId);
        AccountChangeRequest password = new(Guid.NewGuid(), target, 3, "Password", Password: "NewSecret-123!");
        await fixture.Service.SubmitAsync(password, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("renamed", fixture.State.Current!.Values[target].Value.Name);
        Assert.Equal(originalHash, fixture.State.Current.Values[target].Value.PasswordHash);

        // Act
        fixture.Commit(password.OperationId);
        AccountChangeRequest disable = new(Guid.NewGuid(), target, 4, "SetEnabled", IsEnabled: false);
        await fixture.Service.SubmitAsync(disable, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        fixture.Commit(disable.OperationId);

        // Assert
        Assert.NotEqual(originalHash, fixture.State.Current!.Values[target].Value.PasswordHash);
        Assert.False(fixture.State.Find(target)!.IsEnabled);

        // Act
        AccountChangeRequest enable = new(Guid.NewGuid(), target, 5, "SetEnabled", IsEnabled: true);
        await fixture.Service.SubmitAsync(enable, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        fixture.Commit(enable.OperationId);
        AccountChangeRequest delete = new(Guid.NewGuid(), target, 6, "Delete");
        await fixture.Service.SubmitAsync(delete, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        fixture.Commit(delete.OperationId);

        // Assert
        Assert.True(fixture.State.Current!.Values[target].Deleted);
        Assert.True(fixture.State.Find(target)!.IsDeleted);
        Assert.Single(fixture.Service.GetUsers().Data!);
        Assert.DoesNotContain("NewSecret", JsonSerializer.Serialize(fixture.Service.Find(password.OperationId)));
    }

    [Fact]
    public async Task ConcurrentNormalizedLoginsResolveOnceWithoutCredentialDto()
    {
        // Arrange
        SharedAccountFixture fixture = new();
        AccountChangeRequest first = new(Guid.NewGuid(), Guid.NewGuid(), 0, "Create", "same", "SecretOne-123!");
        AccountChangeRequest second = new(Guid.NewGuid(), Guid.NewGuid(), 0, "Create", " ＳＡＭＥ ", "SecretTwo-123!");

        // Act
        await Task.WhenAll(fixture.Service.SubmitAsync(first, fixture.Scenario.Node, TestContext.Current.CancellationToken),
            fixture.Create(fixture.Scenario.Actor).SubmitAsync(second, fixture.Scenario.Node, TestContext.Current.CancellationToken));
        CommandResult winner = fixture.Commit(first.OperationId);
        CommandResult loser = fixture.Commit(second.OperationId);
        string response = JsonSerializer.Serialize(fixture.Service.Find(second.OperationId));

        // Assert
        Assert.Equal(OperationOutcome.Committed, winner.Outcome);
        Assert.Equal(OperationOutcome.Conflict, loser.Outcome);
        Assert.Null(fixture.State.Find(second.UserId));
        Assert.Contains("Conflict", response);
        Assert.DoesNotContain("prepared-", response);
        Assert.DoesNotContain("PasswordHash", response);
        Assert.DoesNotContain("SecurityStamp", response);
        Assert.DoesNotContain("SecretTwo", response);
    }

    [Fact]
    public async Task ConcurrentDeactivationsReauthorizeActorAndKeepLastAdministrator()
    {
        // Arrange
        SharedAccountFixture fixture = new();
        SharedUserManagementService other = fixture.Create(fixture.Scenario.OtherActor);
        AccountChangeRequest first = new(Guid.NewGuid(), fixture.Scenario.OtherActor, 2, "SetEnabled", IsEnabled: false);
        AccountChangeRequest second = new(Guid.NewGuid(), fixture.Scenario.Actor, 1, "Delete");

        // Act
        await fixture.Service.SubmitAsync(first, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        await other.SubmitAsync(second, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        CommandResult accepted = fixture.Commit(first.OperationId);
        CommandResult denied = fixture.Commit(second.OperationId);
        ServiceResult<AccountOperationDto> retry = await other.SubmitAsync(second, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(OperationOutcome.Committed, accepted.Outcome);
        Assert.Equal(OperationOutcome.Rejected, denied.Outcome);
        Assert.Equal("ActorNotActive", denied.Reason);
        Assert.Equal(ServiceErrorType.Forbidden, retry.Error!.Type);
        Assert.True(fixture.State.Find(fixture.Scenario.Actor)!.IsEnabled);
        Assert.Single(fixture.State.Current!.Values.Values, value => value.Value.Enabled && !value.Deleted);
    }

    [Fact]
    public async Task SelfDisableStaleBaseAndChangedRetryAreDeniedWithoutAnotherPending()
    {
        // Arrange
        SharedAccountFixture fixture = new();
        AccountChangeRequest self = new(Guid.NewGuid(), fixture.Scenario.Actor, 1, "SetEnabled", IsEnabled: false);
        AccountChangeRequest stale = new(Guid.NewGuid(), fixture.Scenario.OtherActor, 1, "Update", "wrong");
        AccountChangeRequest create = new(Guid.NewGuid(), Guid.NewGuid(), 0, "Create", "new", "Secret-123!");

        // Act
        ServiceResult<AccountOperationDto> selfResult = await fixture.Service.SubmitAsync(self, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        ServiceResult<AccountOperationDto> staleResult = await fixture.Service.SubmitAsync(stale, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        await fixture.Service.SubmitAsync(create, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        ServiceResult<AccountOperationDto> reuse = await fixture.Service.SubmitAsync(create with { Password = "Changed-123!" },
            fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ServiceErrorType.Conflict, selfResult.Error!.Type);
        Assert.Equal(ServiceErrorType.Conflict, staleResult.Error!.Type);
        Assert.Equal(ServiceErrorType.Conflict, reuse.Error!.Type);
        Assert.Single(fixture.Work.Items);
        Assert.Equal(1, fixture.Passwords.Preparations);
    }

    [Fact]
    public async Task RecoveryAndRestoredPendingRequireExplicitReconciliation()
    {
        // Arrange
        SharedAccountFixture fixture = new();
        AccountChangeRequest request = new(Guid.NewGuid(), Guid.NewGuid(), 0, "Create", "new", "Secret-123!");
        await fixture.Service.SubmitAsync(request, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        fixture.Work.Items[request.OperationId].RequireConfirmationAfterRestore();

        // Act
        ServiceResult<AccountOperationDto> held = await fixture.Service.SubmitAsync(request, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        fixture.State.BeginRecovery();
        ServiceResult<AccountOperationDto> recovering = await fixture.Service.SubmitAsync(request, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ServiceErrorType.Conflict, held.Error!.Type);
        Assert.Equal(ServiceErrorType.Forbidden, recovering.Error!.Type);
        Assert.Single(fixture.Work.Items);
        Assert.Equal(PendingState.NeedsUserConfirmation, fixture.Work.Items[request.OperationId].State);
    }
}
