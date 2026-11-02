using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Domain.Monitoring;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class RevisionReducerTests
{
    [Fact]
    public void ProjectionWriteSet_ContainsBothTypesInOneEntityCollection()
    {
        // Arrange
        RevisionScenario scenario = new();
        RevisionImage before = scenario.State;
        RevisionCommand server = scenario.Server();
        scenario.Apply(server);
        RevisionCommand application = scenario.Command(Guid.NewGuid(), DeletedEntityKind.Container, 0,
            new ConfigurationValue
            {
                Name = "Orders", ParentId = server.EntityId, Endpoint = "https://orders.invalid/health",
                Generation = Guid.NewGuid()
            });
        scenario.Apply(application);

        // Act
        ProjectionWriteSet changes = RevisionWriteSetFactory.Create(before, scenario.State, Guid.NewGuid());

        // Assert
        Assert.Equal(2, changes.Servers.Count);
        MonitoredServer host = Assert.Single(changes.Servers, item => item.Type == ServerType.Server);
        MonitoredServer app = Assert.Single(changes.Servers, item => item.Type == ServerType.Application);
        Assert.Equal(server.EntityId, host.Id);
        Assert.Equal(application.EntityId, app.Id);
        Assert.Equal(host.Id, app.ServerId);
        Assert.Equal("Orders", app.Name);
    }

    [Fact]
    public void LostResponseAndNewLeaderReturnOriginalResultWithoutAnotherEntityRevision()
    {
        // Arrange
        RevisionScenario scenario = new();
        RevisionCommand command = scenario.Server();
        CommandResult original = scenario.Apply(command);

        // Act
        CommandResult repeated = scenario.Apply(command, term: 2);

        // Assert
        Assert.Equal(original, repeated);
        Assert.Equal(original.Index, scenario.State.Values[command.EntityId].Revision);
        Assert.Single(scenario.State.Results);
        Assert.Equal(2, scenario.State.Term);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmedControlBlocksLateOriginalAndSurvivesReplay(bool replace)
    {
        // Arrange
        RevisionScenario scenario = new();
        RevisionImage initial = scenario.State;
        RevisionCommand original = scenario.Server(name: "B");
        RevisionCommand? replacement = null;
        if (replace)
        {
            replacement = scenario.Server(original.EntityId, name: "C");
        }

        RevisionCommand control = scenario.Control(original, replacement);
        scenario.Apply(control);

        // Act
        CommandResult late = scenario.Apply(original);
        RevisionImage replay = scenario.Reducer.Apply(initial, control, 3, 1);
        replay = scenario.Reducer.Apply(replay, original, 4, 1);

        // Assert
        Assert.Equal(scenario.State.Results[original.OperationId], replay.Results[original.OperationId]);
        Assert.Single(scenario.State.Controls);
        if (replace)
        {
            Assert.Equal(OperationOutcome.Replaced, late.Outcome);
            Assert.Equal("C", scenario.State.Values[original.EntityId].Value.Name);
            Assert.Equal(replacement!.OperationId, late.ReplacedBy);
        }
        else
        {
            Assert.Equal(OperationOutcome.Cancelled, late.Outcome);
            Assert.False(scenario.State.Values.ContainsKey(original.EntityId));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptanceWinsControlRaceWithoutFalseCancellationOrReplacement(bool replace)
    {
        // Arrange
        RevisionScenario scenario = new();
        RevisionCommand original = scenario.Server(name: "B");
        CommandResult accepted = scenario.Apply(original);
        RevisionCommand? replacement = null;
        if (replace)
        {
            replacement = scenario.Server(original.EntityId, name: "C");
        }

        // Act
        CommandResult result = scenario.Apply(scenario.Control(original, replacement));

        // Assert
        Assert.Equal(OperationOutcome.Committed, result.Outcome);
        Assert.Equal(accepted.Index, result.Index);
        Assert.Equal("B", scenario.State.Values[original.EntityId].Value.Name);
        Assert.Empty(scenario.State.Controls);
        if (replacement is not null)
        {
            Assert.False(scenario.State.Results.ContainsKey(replacement.OperationId));
        }
    }

    [Fact]
    public void ConflictRetainsBaseAndBothVariantsUntilNewAuthorizedResolution()
    {
        // Arrange
        RevisionScenario scenario = new();
        RevisionCommand create = scenario.Server(name: "A");
        scenario.Apply(create);
        RevisionCommand first = scenario.Server(create.EntityId, 3, "B");
        RevisionCommand second = scenario.Server(create.EntityId, 3, "C") with { CreatedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-1) };
        scenario.Apply(first);

        // Act
        CommandResult conflict = scenario.Apply(second);

        // Assert
        Assert.Equal(OperationOutcome.Conflict, conflict.Outcome);
        Assert.Equal("B", scenario.State.Values[create.EntityId].Value.Name);
        ConflictVariant variants = scenario.State.Conflicts[conflict.ConflictId!.Value];
        Assert.Contains("\"A\"", variants.BasePayload);
        Assert.Contains("\"B\"", variants.AcceptedPayload);
        Assert.Contains("\"C\"", variants.ProposedPayload);
        Assert.Equal(3, variants.BaseRevision);
        Assert.Equal(4, variants.CurrentRevision);

        // Act
        RevisionCommand resolve = scenario.Server(create.EntityId, 4, "C") with
        {
            ResolvesConflictId = conflict.ConflictId, CreatedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-2)
        };
        scenario.Apply(resolve);

        // Assert
        Assert.Equal("C", scenario.State.Values[create.EntityId].Value.Name);
        Assert.Equal(resolve.OperationId, scenario.State.Conflicts[variants.Id].ResolutionOperationId);
    }

    [Fact]
    public void DeletedAccountReservesLoginAndCredentialChangeRequiresNewStamp()
    {
        // Arrange
        RevisionScenario scenario = new();
        RevisionCommand account = scenario.Command(Guid.NewGuid(), DeletedEntityKind.Account, 0, scenario.AccountValue("retired"));
        scenario.Apply(account);

        // Act
        CommandResult credential = scenario.Apply(scenario.Command(account.EntityId, DeletedEntityKind.Account, 3,
            account.Value! with { PasswordHash = "new-protected-hash" }));
        scenario.Apply(scenario.Command(account.EntityId, DeletedEntityKind.Account, 3, null) with { Action = "Delete" });
        CommandResult reuse = scenario.Apply(scenario.Command(Guid.NewGuid(), DeletedEntityKind.Account, 0, account.Value));

        // Assert
        Assert.Equal(OperationOutcome.Rejected, credential.Outcome);
        Assert.Equal("CredentialStampRequired", credential.Reason);
        Assert.True(scenario.State.Values[account.EntityId].Deleted);
        Assert.Equal(OperationOutcome.Conflict, reuse.Outcome);
    }

    [Fact]
    public void EndpointChangeRequiresNewGenerationAndMetadataChangePreservesIt()
    {
        // Arrange
        RevisionScenario scenario = new();
        RevisionCommand create = scenario.Server();
        scenario.Apply(create);
        RevisionCommand endpoint = scenario.Server(create.EntityId, 3) with
        {
            Value = create.Value! with { Endpoint = "https://changed.invalid/health" }
        };

        // Act
        CommandResult invalid = scenario.Apply(endpoint);
        CommandResult changed = scenario.Apply(endpoint with
        {
            OperationId = Guid.NewGuid(), Value = endpoint.Value! with { Generation = Guid.NewGuid() }
        });
        RevisionCommand metadata = scenario.Server(create.EntityId, changed.AppliedRevision!.Value, "renamed") with
        {
            Value = scenario.State.Values[create.EntityId].Value with { Name = "renamed" }
        };
        CommandResult renamed = scenario.Apply(metadata);

        // Assert
        Assert.Equal(OperationOutcome.Rejected, invalid.Outcome);
        Assert.Equal("CheckGenerationMismatch", invalid.Reason);
        Assert.Equal(OperationOutcome.Committed, changed.Outcome);
        Assert.Equal(OperationOutcome.Committed, renamed.Outcome);
        Assert.NotEqual(create.Value!.Generation, scenario.State.Values[create.EntityId].Value.Generation);
        Assert.Equal(metadata.Value!.Generation, scenario.State.Values[create.EntityId].Value.Generation);
    }

    [Theory]
    [InlineData(DeletedEntityKind.Account)]
    [InlineData(DeletedEntityKind.Server)]
    [InlineData(DeletedEntityKind.Chat)]
    public void DuplicateKeysKeepBothVersionsEvenWhenCompetingRecordIsOlderThanBase(DeletedEntityKind kind)
    {
        // Arrange
        RevisionScenario scenario = new();
        ConfigurationValue firstValue = scenario.AccountValue("duplicate") with { TelegramChatId = 123, Endpoint = "https://a.invalid", Generation = Guid.NewGuid() };
        ConfigurationValue otherValue = firstValue with { Name = "other-name", TelegramChatId = 456 };
        RevisionCommand first = scenario.Command(Guid.NewGuid(), kind, 0, firstValue);
        scenario.Apply(first);
        RevisionCommand second = scenario.Command(Guid.NewGuid(), kind, 0, otherValue);
        scenario.Apply(second);

        // Act
        CommandResult result = scenario.Apply(scenario.Command(second.EntityId, kind, 4, firstValue));

        // Assert
        Assert.Equal(OperationOutcome.Conflict, result.Outcome);
        ConflictVariant conflict = scenario.State.Conflicts[result.ConflictId!.Value];
        Assert.Equal(4, conflict.BaseRevision);
        Assert.Equal(3, conflict.CurrentRevision);
        Assert.Equal(otherValue, scenario.State.Values[second.EntityId].Value);
    }

    [Fact]
    public void ActorIsReauthorizedAfterAcceptedDisableAndClaimsCannotKeepAccess()
    {
        // Arrange
        RevisionScenario scenario = new();
        RevisionCommand pending = scenario.Server();
        AcceptedValue actor = scenario.State.Values[scenario.Actor];
        RevisionCommand disable = scenario.Command(scenario.Actor, DeletedEntityKind.Account, actor.Revision,
            actor.Value with { Enabled = false }) with { ActorUserId = scenario.OtherActor, ActorRevision = 2 };
        scenario.Apply(disable);

        // Act
        CommandResult result = scenario.Apply(pending);

        // Assert
        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.Equal("ActorNotActive", result.Reason);
        Assert.False(scenario.State.Values.ContainsKey(pending.EntityId));
    }

    [Fact]
    public void DeleteRetainsContainerAndBlocksResurrectionAndDependentPending()
    {
        // Arrange
        RevisionScenario scenario = new();
        RevisionCommand server = scenario.Server();
        scenario.Apply(server);
        RevisionCommand container = scenario.Command(Guid.NewGuid(), DeletedEntityKind.Container, 0,
            new ConfigurationValue { ParentId = server.EntityId, Endpoint = "https://container.invalid", Generation = Guid.NewGuid() });
        scenario.Apply(container);
        RevisionCommand dependentPending = scenario.Command(Guid.NewGuid(), DeletedEntityKind.Container, 0, container.Value);
        RevisionCommand delete = scenario.Server(server.EntityId, 3) with { Action = "Delete", Value = null };

        // Act
        scenario.Apply(delete);
        CommandResult late = scenario.Apply(scenario.Server(server.EntityId, 3));
        CommandResult dependent = scenario.Apply(dependentPending);

        // Assert
        Assert.True(scenario.State.Values[server.EntityId].Deleted);
        Assert.False(scenario.State.Values[container.EntityId].Deleted);
        Assert.Null(scenario.State.Values[container.EntityId].Value.ParentId);
        Assert.Equal(OperationOutcome.Conflict, late.Outcome);
        Assert.Equal(OperationOutcome.Cancelled, dependent.Outcome);
        Assert.Equal("DependencyDeleted", dependent.Reason);
    }

    [Fact]
    public void InvalidDomainValueIsDurablyRejectedAndIdReuseFailsClosed()
    {
        // Arrange
        RevisionScenario scenario = new();
        RevisionCommand command = scenario.Server() with { Value = new ConfigurationValue() };

        // Act
        CommandResult result = scenario.Apply(command);

        // Assert
        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.DoesNotContain(scenario.State.Values.Values, value => value.Kind == DeletedEntityKind.Server);

        // Act
        Exception? reused = Record.Exception(() => scenario.Apply(command with { Value = scenario.Server().Value }));

        // Assert
        Assert.IsType<InvalidDataException>(reused);
    }
}
