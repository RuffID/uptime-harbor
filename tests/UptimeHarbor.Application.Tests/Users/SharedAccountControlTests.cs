using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Users.Models;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Users;

public class SharedAccountControlTests
{
    [Fact]
    public async Task HeldAccountRequiresExplicitReplacement_WithNewPasswordAndStableRetry()
    {
        // Arrange
        SharedAccountFixture fixture = new();
        AccountChangeRequest original = new(Guid.NewGuid(), Guid.NewGuid(), 0, "Create", "new-admin", "Secret-123!");
        await fixture.Service.SubmitAsync(original, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        fixture.Work.Items[original.OperationId].RequireConfirmationAfterRestore();
        AccountChangeRequest renewed = original with { OperationId = Guid.NewGuid(), ControlOperationId = Guid.NewGuid(),
            TargetOperationId = original.OperationId, Password = "Renewed-123!" };

        // Act
        ServiceResult<AccountOperationDto> denied = await fixture.Service.SubmitAsync(renewed, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        ServiceResult<AccountOperationDto> submitted = await fixture.Service.SubmitAsync(renewed with { ConfirmRestored = true }, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        ServiceResult<AccountOperationDto> retry = await fixture.Service.SubmitAsync(renewed with { ConfirmRestored = true }, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ServiceErrorType.Conflict, denied.Error!.Type);
        Assert.Equal("Pending", submitted.Data!.Status);
        Assert.Equal(submitted.Data, retry.Data);
        Assert.Equal(PendingState.NeedsUserConfirmation, fixture.Work.Items[original.OperationId].State);
        Assert.Equal(2, fixture.Work.Items.Count);
        Assert.Null(fixture.State.Find(original.UserId));

        // Act
        Assert.Equal(OperationOutcome.Replaced, fixture.Commit(renewed.ControlOperationId!.Value).Outcome);
        ServiceResult<AccountOperationDto> result = await fixture.Service.SubmitAsync(renewed with { ConfirmRestored = true }, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Committed", result.Data!.Outcome!.ReplacementResult!.Status);
        Assert.True(fixture.State.Find(original.UserId)!.IsEnabled);
        ConfigurationValue accepted = fixture.State.Current!.Values[original.UserId].Value;
        Assert.True(fixture.Passwords.Verify(original.UserId, "new-admin", new(
            accepted.PasswordHash!, 1, accepted.SecurityStamp!, accepted.ConcurrencyStamp!), "Renewed-123!"));
    }

    [Fact]
    public async Task AccountConflictResolutionUsesNewCurrentCommand_AndDisabledActorIsRejected()
    {
        // Arrange
        SharedAccountFixture fixture = new();
        AccountChangeRequest first = new(Guid.NewGuid(), Guid.NewGuid(), 0, "Create", "same", "First-123!");
        AccountChangeRequest losing = new(Guid.NewGuid(), Guid.NewGuid(), 0, "Create", "same", "Second-123!");
        await fixture.Service.SubmitAsync(first, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        await fixture.Service.SubmitAsync(losing, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcome.Committed, fixture.Commit(first.OperationId).Outcome);
        CommandResult conflict = fixture.Commit(losing.OperationId);
        Assert.Equal(OperationOutcome.Conflict, conflict.Outcome);
        AccountChangeRequest solution = losing with { OperationId = Guid.NewGuid(), Login = "different", ResolvesConflictId = conflict.ConflictId };

        // Act
        ServiceResult<AccountOperationDto> submitted = await fixture.Service.SubmitAsync(solution, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcome.Committed, fixture.Commit(solution.OperationId).Outcome);

        // Assert
        Assert.Equal("Pending", submitted.Data!.Status);
        Assert.Equal("same", fixture.State.Current!.Values[first.UserId].Value.Name);
        Assert.Equal("different", fixture.State.Current.Values[losing.UserId].Value.Name);
        Assert.Equal(solution.OperationId, fixture.State.Current!.Conflicts[conflict.ConflictId!.Value].ResolutionOperationId);

        // Arrange
        AcceptedValue actor = fixture.State.Current.Values[fixture.Scenario.Actor];
        fixture.State.Publish(fixture.State.Current with { Values = fixture.State.Current.Values.SetItem(actor.Id,
            actor with { Value = actor.Value with { Enabled = false } }) });

        // Act
        ServiceResult<AccountOperationDto> denied = await fixture.Service.SubmitAsync(solution with { OperationId = Guid.NewGuid() }, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ServiceErrorType.Forbidden, denied.Error!.Type);
        Assert.Equal(3, fixture.Work.Items.Count);
    }
}
