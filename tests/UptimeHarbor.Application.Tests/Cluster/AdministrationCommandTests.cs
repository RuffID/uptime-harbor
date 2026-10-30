using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class AdministrationCommandTests
{
    [Fact]
    public async Task PendingIsNotAccepted_AndLostResponseRetryRetainsIdentityAndOutcome()
    {
        // Arrange
        AdministrationFixture fixture = new();
        ConfigurationChangeRequest request = fixture.Server();
        ClusterConfigurationCommands service = fixture.Commands();

        // Act
        ServiceResult<AdministrationOperationDto> submitted = await service.SubmitAsync(request, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        ServiceResult<AdministrationOperationDto> retry = await fixture.Commands().SubmitAsync(request, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        ServiceResult<AdministrationOperationDto> changed = await service.SubmitAsync(request with { Settings = request.Settings! with { Name = "different" } }, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("LocalPending", submitted.Data!.Status);
        Assert.Equal(submitted.Data, retry.Data);
        Assert.Equal(ServiceErrorType.Conflict, changed.Error!.Type);
        Assert.DoesNotContain(fixture.Query().Read(fixture.Scenario.Node).Data!.Entities, entity => entity.Id == request.EntityId);
        Assert.Single(fixture.Work.Items);

        // Act
        Assert.Equal(OperationOutcome.Committed, fixture.Commit(request.OperationId).Outcome);
        ServiceResult<AdministrationOperationDto> accepted = await service.SubmitAsync(request, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Committed", accepted.Data!.Status);
        Assert.Equal(3, accepted.Data.AppliedRevision);
        Assert.Single(fixture.Work.Items);
    }

    [Fact]
    public async Task ReplacementHasItsOwnOutcome_AndOriginalIsNeverSilentlyEdited()
    {
        // Arrange
        AdministrationFixture fixture = new();
        ConfigurationChangeRequest original = fixture.Server();
        await fixture.Commands().SubmitAsync(original, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        ConfigurationChangeRequest replacement = original with
        {
            OperationId = Guid.NewGuid(), ControlOperationId = Guid.NewGuid(), TargetOperationId = original.OperationId,
            Settings = original.Settings! with { Name = "replacement" }
        };

        // Act
        ServiceResult<AdministrationOperationDto> submitted = await fixture.Commands().SubmitAsync(replacement, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        Assert.Equal("LocalPending", submitted.Data!.Status);
        CommandResult decision = fixture.Commit(replacement.ControlOperationId!.Value);
        AdministrationOperationDto result = fixture.Pending().Find(replacement.ControlOperationId.Value).Data!;

        // Assert
        Assert.Equal(OperationOutcome.Replaced, decision.Outcome);
        Assert.Equal("Committed", result.ReplacementResult!.Status);
        Assert.Equal("replacement", fixture.State.Current!.Values[original.EntityId].Value.Name);
        Assert.Equal("server", fixture.Command(original.OperationId).Value!.Name);
        Assert.Equal("Replaced", fixture.Pending().Find(original.OperationId).Data!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptanceWinningControlRaceReportsCommittedWithoutRollback(bool replace)
    {
        // Arrange
        AdministrationFixture fixture = new();
        ConfigurationChangeRequest original = fixture.Server();
        await fixture.Commands().SubmitAsync(original, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcome.Committed, fixture.Commit(original.OperationId).Outcome);
        Guid control = Guid.NewGuid();
        Guid newOperation = Guid.NewGuid();

        // Act
        if (replace)
        {
            ConfigurationChangeRequest request = original with { OperationId = newOperation,
                TargetOperationId = original.OperationId, ControlOperationId = control,
                Settings = original.Settings! with { Name = "must-not-apply" } };
            Assert.True((await fixture.Commands().SubmitAsync(request, fixture.Scenario.Node, TestContext.Current.CancellationToken)).Success);
        }
        else
        {
            Assert.True((await fixture.Commands().CancelAsync(original.OperationId, control, fixture.Scenario.Node, TestContext.Current.CancellationToken)).Success);
        }
        Assert.Equal(OperationOutcome.Committed, fixture.Commit(control).Outcome);
        AdministrationOperationDto result = fixture.Pending().Find(control).Data!;

        // Assert
        Assert.Equal("Committed", result.Status);
        Assert.Contains("уже решена", result.Message);
        Assert.Null(result.ReplacementResult);
        Assert.DoesNotContain(newOperation, fixture.State.Current!.Results.Keys);
        Assert.Equal("server", fixture.State.Current.Values[original.EntityId].Value.Name);
    }

    [Fact]
    public async Task HeldBackupRequiresExplicitFreshReplacement_AndKnownOutcomeWinsLocalHeldState()
    {
        // Arrange
        AdministrationFixture fixture = new();
        ConfigurationChangeRequest original = fixture.Server();
        await fixture.Commands().SubmitAsync(original, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        fixture.Work.Items[original.OperationId].RequireConfirmationAfterRestore();
        ConfigurationChangeRequest replacement = original with { OperationId = Guid.NewGuid(), TargetOperationId = original.OperationId,
            ControlOperationId = Guid.NewGuid() };

        // Act
        ServiceResult<AdministrationOperationDto> unconfirmed = await fixture.Commands().SubmitAsync(replacement, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        ServiceResult<AdministrationOperationDto> stale = await fixture.Commands().SubmitAsync(replacement with { BaseRevision = 99, ConfirmRestored = true }, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        ServiceResult<AdministrationOperationDto> confirmed = await fixture.Commands().SubmitAsync(replacement with { ConfirmRestored = true }, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ServiceErrorType.Conflict, unconfirmed.Error!.Type);
        Assert.Equal(ServiceErrorType.Conflict, stale.Error!.Type);
        Assert.Equal("LocalPending", confirmed.Data!.Status);
        Assert.Equal(PendingState.NeedsUserConfirmation, fixture.Work.Items[original.OperationId].State);
        Assert.Equal(2, fixture.Work.Items.Count);

        // Act
        Assert.Equal(OperationOutcome.Replaced, fixture.Commit(replacement.ControlOperationId!.Value).Outcome);
        IReadOnlyList<AdministrationPendingDto> page = (await fixture.Pending().ReadAsync(0, TestContext.Current.CancellationToken)).Data!;

        // Assert
        AdministrationPendingDto old = Assert.Single(page, row => row.OperationId == original.OperationId);
        Assert.Equal("Resolved", old.State);
        Assert.Equal("Replaced", old.Result!.Status);
        Assert.Equal(PendingState.NeedsUserConfirmation, fixture.Work.Items[original.OperationId].State);
    }

    [Fact]
    public async Task HeldCancellationDoesNotReleaseOldPayload_AndDisableDeniesFurtherControls()
    {
        // Arrange
        AdministrationFixture fixture = new();
        ConfigurationChangeRequest original = fixture.Server();
        await fixture.Commands().SubmitAsync(original, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        fixture.Work.Items[original.OperationId].RequireConfirmationAfterRestore();
        Guid cancelId = Guid.NewGuid();

        // Act
        Assert.Equal("LocalPending", (await fixture.Commands().CancelAsync(original.OperationId, cancelId, fixture.Scenario.Node, TestContext.Current.CancellationToken)).Data!.Status);
        Assert.Equal(OperationOutcome.Cancelled, fixture.Commit(cancelId).Outcome);
        AcceptedValue actor = fixture.State.Current!.Values[fixture.Scenario.Actor];
        fixture.State.Publish(fixture.State.Current with { Values = fixture.State.Current.Values.SetItem(actor.Id,
            actor with { Value = actor.Value with { Enabled = false } }) });
        ServiceResult<AdministrationOperationDto> denied = await fixture.Commands().CancelAsync(original.OperationId, Guid.NewGuid(), fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PendingState.NeedsUserConfirmation, fixture.Work.Items[original.OperationId].State);
        Assert.DoesNotContain(original.EntityId, fixture.State.Current!.Values.Keys);
        Assert.Equal(ServiceErrorType.Forbidden, denied.Error!.Type);
        Assert.Equal(2, fixture.Work.Items.Count);
    }

    [Fact]
    public async Task ConflictResolutionUsesCurrentRevision_AndCanConflictAgainWithoutOverwritingWinner()
    {
        // Arrange
        AdministrationFixture fixture = new();
        ConfigurationChangeRequest original = fixture.Server();
        await fixture.Commands().SubmitAsync(original, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        fixture.Commit(original.OperationId);
        ConfigurationChangeRequest losing = original with { OperationId = Guid.NewGuid(), Settings = original.Settings! with { Name = "proposal" } };
        await fixture.Commands().SubmitAsync(losing, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcome.Conflict, fixture.Commit(losing.OperationId).Outcome);
        AdministrationConflictDto conflict = Assert.Single(fixture.Query().Read(fixture.Scenario.Node).Data!.Conflicts);
        ConfigurationChangeRequest solution = losing with { OperationId = Guid.NewGuid(), BaseRevision = conflict.CurrentRevision, ResolvesConflictId = conflict.Id };
        await fixture.Commands().SubmitAsync(solution, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        ConfigurationChangeRequest concurrent = original with { OperationId = Guid.NewGuid(), BaseRevision = conflict.CurrentRevision,
            Settings = original.Settings! with { Name = "concurrent" } };
        await fixture.Commands().SubmitAsync(concurrent, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Act
        Assert.Equal(OperationOutcome.Committed, fixture.Commit(concurrent.OperationId).Outcome);
        Assert.Equal(OperationOutcome.Conflict, fixture.Commit(solution.OperationId).Outcome);
        ConfigurationChangeRequest fresh = solution with { OperationId = Guid.NewGuid(), BaseRevision = fixture.State.Current!.Values[original.EntityId].Revision };
        await fixture.Commands().SubmitAsync(fresh, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcome.Committed, fixture.Commit(fresh.OperationId).Outcome);

        // Assert
        Assert.Equal("proposal", fixture.State.Current!.Values[original.EntityId].Value.Name);
        Assert.Equal(fresh.OperationId, fixture.State.Current.Conflicts[conflict.Id].ResolutionOperationId);
        Assert.Equal("proposal", fixture.Command(solution.OperationId).Value!.Name);
    }

    [Fact]
    public async Task GlobalDeletionNeedsConfirmation_AndPendingNeverLooksDeleted()
    {
        // Arrange
        AdministrationFixture fixture = new();
        ConfigurationChangeRequest original = fixture.Server();
        await fixture.Commands().SubmitAsync(original, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        fixture.Commit(original.OperationId);
        ConfigurationChangeRequest delete = original with { OperationId = Guid.NewGuid(), BaseRevision = 3, Action = "Delete", Settings = null };

        // Act
        ServiceResult<AdministrationOperationDto> denied = await fixture.Commands().SubmitAsync(delete, fixture.Scenario.Node, TestContext.Current.CancellationToken);
        ServiceResult<AdministrationOperationDto> submitted = await fixture.Commands().SubmitAsync(delete with { ConfirmGlobalDelete = true }, fixture.Scenario.Node, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ServiceErrorType.Validation, denied.Error!.Type);
        Assert.Equal("LocalPending", submitted.Data!.Status);
        Assert.False(fixture.State.Current!.Values[original.EntityId].Deleted);
        Assert.Equal(OperationOutcome.Committed, fixture.Commit(delete.OperationId).Outcome);
        Assert.True(fixture.State.Current.Values[original.EntityId].Deleted);
    }
}
