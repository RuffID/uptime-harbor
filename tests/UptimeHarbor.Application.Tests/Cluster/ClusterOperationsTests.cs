using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class ClusterOperationsTests
{
    [Fact]
    public void AdmissionUsesAcceptedAdministratorAndDoesNotActivateOrAddVote()
    {
        // Arrange
        Fixture fixture = new();
        RevisionCommand admission = fixture.Admission();

        // Act
        CommandResult result = fixture.Scenario.Apply(admission);

        // Assert
        Assert.Equal(OperationOutcome.Committed, result.Outcome);
        OperationalNode node = fixture.Scenario.State.Operations.Nodes[admission.EntityId];
        Assert.False(node.Voting);
        Assert.False(node.Activated);
        Assert.Equal(admission.Operation!.Node!.StorageGeneration, fixture.Scenario.State.Monitoring.Nodes[node.NodeId]);
        Assert.Equal(2, fixture.Scenario.State.Values.Count);
    }

    [Fact]
    public void DisabledActorAndClonedIdentityAreRejectedAtAcceptance()
    {
        // Arrange
        Fixture fixture = new();
        RevisionCommand admission = fixture.Admission();
        AcceptedValue actor = fixture.Scenario.State.Values[fixture.Scenario.Actor];
        fixture.Scenario.State = fixture.Scenario.State with
        {
            Values = fixture.Scenario.State.Values.SetItem(actor.Id, actor with { Value = actor.Value with { Enabled = false } })
        };

        // Act
        CommandResult denied = fixture.Scenario.Apply(admission);

        // Assert
        Assert.Equal("ActorNotActive", denied.Reason);
        Assert.Single(fixture.Scenario.State.Operations.Nodes);

        // Arrange
        Fixture other = new();
        RevisionCommand clone = other.Admission(other.Scenario.Node);

        // Act
        CommandResult duplicate = other.Scenario.Apply(clone);

        // Assert
        Assert.Equal("DuplicateNodeIdentity", duplicate.Reason);
        Assert.Single(other.Scenario.State.Operations.Nodes);
    }

    [Fact]
    public void CancelledAdmissionCannotBeResurrectedByLatePending()
    {
        // Arrange
        Fixture fixture = new();
        RevisionCommand admission = fixture.Admission();
        RevisionCommand cancel = fixture.Scenario.Control(admission);

        // Act
        CommandResult cancelled = fixture.Scenario.Apply(cancel);
        CommandResult late = fixture.Scenario.Apply(admission);

        // Assert
        Assert.Equal(OperationOutcome.Cancelled, cancelled.Outcome);
        Assert.Equal(OperationOutcome.Cancelled, late.Outcome);
        Assert.Single(fixture.Scenario.State.Operations.Nodes);
        Assert.Single(fixture.Scenario.State.Controls);
    }

    [Fact]
    public void MembershipRequiresAcceptedIntentAndRechecksActorBeforeConfirmation()
    {
        // Arrange
        Fixture fixture = new();
        RevisionCommand admission = fixture.Admission();
        Assert.Equal(OperationOutcome.Committed, fixture.Scenario.Apply(admission).Outcome);
        RevisionCommand request = fixture.Command("RequestMembership", admission.EntityId);
        Assert.Equal(OperationOutcome.Committed, fixture.Scenario.Apply(request).Outcome);
        RevisionCommand confirm = fixture.Internal("ConfirmMembership", admission.EntityId,
            new ClusterOperation { ChangeId = request.OperationId, MembershipVersion = 2 });
        AcceptedValue actor = fixture.Scenario.State.Values[fixture.Scenario.Actor];
        fixture.Scenario.State = fixture.Scenario.State with
        {
            Values = fixture.Scenario.State.Values.SetItem(actor.Id, actor with { Revision = actor.Revision + 1 })
        };

        // Act
        CommandResult result = fixture.Scenario.Apply(confirm);

        // Assert
        Assert.Equal("ActorNotActive", result.Reason);
        Assert.False(fixture.Scenario.State.Operations.Nodes[admission.EntityId].Voting);
    }

    [Fact]
    public void AnotherCurrentAdministratorCanAuthorizePendingNativeResultAfterActorChanges()
    {
        // Arrange
        Fixture fixture = new();
        RevisionCommand admission = fixture.Admission();
        fixture.Scenario.Apply(admission);
        RevisionCommand request = fixture.Command("RequestMembership", admission.EntityId);
        fixture.Scenario.Apply(request);
        AcceptedValue actor = fixture.Scenario.State.Values[fixture.Scenario.Actor];
        fixture.Scenario.State = fixture.Scenario.State with
        {
            Values = fixture.Scenario.State.Values.SetItem(actor.Id, actor with
            {
                Revision = actor.Revision + 1, Value = actor.Value with { Enabled = false }
            })
        };
        RevisionCommand authorize = fixture.Command("AuthorizeMembership", admission.EntityId) with
        {
            ActorUserId = fixture.Scenario.OtherActor, ActorRevision = 2,
            Operation = new ClusterOperation { StorageGeneration = fixture.Storage, ChangeId = request.OperationId, MembershipVersion = 2 }
        };

        // Act
        CommandResult authorization = fixture.Scenario.Apply(authorize);
        CommandResult confirmation = fixture.Scenario.Apply(fixture.Internal("ConfirmMembership", admission.EntityId,
            new ClusterOperation { ChangeId = request.OperationId, MembershipVersion = 2 }));

        // Assert
        Assert.Equal(OperationOutcome.Committed, authorization.Outcome);
        Assert.Equal(OperationOutcome.Committed, confirmation.Outcome);
        Assert.True(fixture.Scenario.State.Operations.Nodes[admission.EntityId].Voting);
        Assert.Equal(fixture.Scenario.OtherActor, fixture.Scenario.State.Operations.Changes[request.OperationId].ActorUserId);
        Assert.Equal(request.CreatedAtUtc, fixture.Scenario.State.Operations.Credentials[admission.Operation!.Credential!.Id].ConsumedAtUtc);
    }

    [Fact]
    public void AcceptedMembershipAndRemovalPreserveAccountsAndHistory()
    {
        // Arrange
        Fixture fixture = new();
        RevisionCommand admission = fixture.Admission();
        Assert.Equal(OperationOutcome.Committed, fixture.Scenario.Apply(admission).Outcome);
        RevisionCommand add = fixture.Command("RequestMembership", admission.EntityId);
        Assert.Equal(OperationOutcome.Committed, fixture.Scenario.Apply(add).Outcome);
        Assert.Equal(OperationOutcome.Committed, fixture.Scenario.Apply(fixture.Internal("ConfirmMembership", admission.EntityId,
            new ClusterOperation { ChangeId = add.OperationId, MembershipVersion = 2 })).Outcome);
        RevisionImage before = fixture.Scenario.State;
        RevisionCommand remove = fixture.Command("RequestMembership", admission.EntityId) with
        {
            Operation = new ClusterOperation { StorageGeneration = fixture.Storage, MembershipVersion = 2, Remove = true }
        };
        Assert.Equal(OperationOutcome.Committed, fixture.Scenario.Apply(remove).Outcome);

        // Act
        CommandResult result = fixture.Scenario.Apply(fixture.Internal("ConfirmMembership", admission.EntityId,
            new ClusterOperation { ChangeId = remove.OperationId, MembershipVersion = 3 }));

        // Assert
        Assert.Equal(OperationOutcome.Committed, result.Outcome);
        Assert.True(fixture.Scenario.State.Operations.Nodes[admission.EntityId].Removed);
        Assert.Equal(before.Values, fixture.Scenario.State.Values);
        Assert.All(before.Results, prior => Assert.Equal(prior.Value, fixture.Scenario.State.Results[prior.Key]));
        Assert.NotNull(fixture.Scenario.State.Operations.Credentials[admission.Operation!.Credential!.Id].RevokedAtUtc);
    }

    [Fact]
    public void LastVoterAndStaleInternalTermCannotChangeWorkingState()
    {
        // Arrange
        Fixture fixture = new();
        RevisionCommand last = fixture.Command("RequestMembership", fixture.Scenario.Node) with
        {
            Operation = new ClusterOperation { StorageGeneration = fixture.Storage, MembershipVersion = 1, Remove = true }
        };

        // Act
        CommandResult denied = fixture.Scenario.Apply(last);
        RevisionCommand stale = fixture.Internal("ActivateNode", fixture.Scenario.Node, new ClusterOperation { MembershipVersion = 1 });
        CommandResult changedTerm = fixture.Scenario.Apply(stale, 2);

        // Assert
        Assert.Equal("LastVoter", denied.Reason);
        Assert.Equal("LifecycleAuthorityChanged", changedTerm.Reason);
        Assert.False(fixture.Scenario.State.Operations.Nodes[fixture.Scenario.Node].Activated);
    }

    [Fact]
    public void MaintenanceClosesEpochAndRequiresExactGenerationToLeave()
    {
        // Arrange
        Fixture fixture = new();
        Guid maintenance = Guid.NewGuid();
        RevisionCommand enter = fixture.Command("EnterMaintenance", fixture.Scenario.Node) with
        {
            Operation = new ClusterOperation { StorageGeneration = fixture.Storage, MembershipVersion = 1, MaintenanceId = maintenance }
        };
        Assert.Equal(OperationOutcome.Committed, fixture.Scenario.Apply(enter).Outcome);

        // Act
        CommandResult activation = fixture.Scenario.Apply(fixture.Internal("ActivateNode", fixture.Scenario.Node,
            new ClusterOperation { MembershipVersion = 1 }));
        CommandResult wrong = fixture.Scenario.Apply(fixture.Internal("LeaveMaintenance", fixture.Scenario.Node,
            new ClusterOperation { MembershipVersion = 1, MaintenanceId = Guid.NewGuid() }));
        CommandResult leave = fixture.Scenario.Apply(fixture.Internal("LeaveMaintenance", fixture.Scenario.Node,
            new ClusterOperation { MembershipVersion = 1, MaintenanceId = maintenance }));

        // Assert
        Assert.Equal("ActivationUnavailable", activation.Reason);
        Assert.Equal("MaintenanceGenerationMismatch", wrong.Reason);
        Assert.Equal(OperationOutcome.Committed, leave.Outcome);
        Assert.Equal(Guid.Empty, fixture.Scenario.State.Operations.MaintenanceId);
        Assert.Null(fixture.Scenario.State.Operations.Sender);
        Assert.False(fixture.Scenario.State.Operations.Nodes[fixture.Scenario.Node].Activated);
    }

    [Fact]
    public void LifecyclePayloadCannotHideExtraCredentialOrPretendToBeAdministrator()
    {
        // Arrange
        Fixture fixture = new();
        RevisionCommand internalCommand = fixture.Internal("ActivateNode", fixture.Scenario.Node,
            new ClusterOperation { MembershipVersion = 1 });
        RevisionCommand fakeActor = internalCommand with { ActorUserId = fixture.Scenario.Actor, ActorRevision = 1 };
        RevisionCommand extra = internalCommand with { Operation = internalCommand.Operation! with { Credential = fixture.Credential } };

        // Act
        Exception? actorFailure = Record.Exception(() => RevisionCodec.Encode(fakeActor));
        Exception? payloadFailure = Record.Exception(() => RevisionCodec.Encode(extra));

        // Assert
        Assert.IsType<InvalidDataException>(actorFailure);
        Assert.IsType<InvalidDataException>(payloadFailure);
    }

    [Fact]
    public void OldCommandEncodingDoesNotAcquireNewNullLifecycleField()
    {
        // Arrange
        RevisionScenario scenario = new();
        RevisionCommand old = scenario.Server();

        // Act
        string encoded = System.Text.Encoding.UTF8.GetString(RevisionCodec.Encode(old));
        RevisionCommand decoded = RevisionCodec.Decode(System.Text.Encoding.UTF8.GetBytes(encoded));

        // Assert
        Assert.DoesNotContain("\"Operation\":", encoded);
        Assert.Equal(RevisionCodec.Digest(old), RevisionCodec.Digest(decoded));
    }

    private class Fixture
    {
        public RevisionScenario Scenario { get; } = new();
        public Guid Storage { get; } = Guid.NewGuid();
        public OperationalCredential Credential { get; }

        public Fixture()
        {
            OperationalNode node = new(Scenario.Node, Storage, "https://first.invalid/internal/raft", "first", true, false, false, 0);
            Credential = new(Guid.NewGuid(), node.NodeId, new string('A', 64), "public certificate", DateTimeOffset.UnixEpoch.AddYears(100), null, null);
            Scenario.State = Scenario.State with
            {
                Operations = new ClusterOperationsImage
                {
                    Nodes = new ClusterOperationsImage().Nodes.Add(node.NodeId, node),
                    Credentials = new ClusterOperationsImage().Credentials.Add(Credential.Id, Credential), BotId = 123, MembershipVersion = 1
                },
                Monitoring = Scenario.State.Monitoring with { Nodes = Scenario.State.Monitoring.Nodes.Add(node.NodeId, Storage) }
            };
        }

        public RevisionCommand Command(string action, Guid nodeId)
        {
            return Scenario.Command(nodeId, DeletedEntityKind.Node, Scenario.State.Operations.Revision, null) with
            {
                Action = action, Operation = new ClusterOperation { StorageGeneration = Storage, MembershipVersion = 1 }
            };
        }

        public RevisionCommand Admission(Guid? id = null)
        {
            Guid nodeId = id ?? Guid.NewGuid();
            RevisionCommand command = Command("AdmitNode", nodeId);
            return command with
            {
                Operation = command.Operation! with
                {
                    Node = new(nodeId, Guid.NewGuid(), "https://second.invalid/internal/raft", "second", false, false, false, 0),
                    Credential = new(Guid.NewGuid(), nodeId, new string('B', 64), "public certificate", DateTimeOffset.UnixEpoch.AddYears(100),
                        command.OperationId, command.CreatedAtUtc.AddMinutes(15))
                }
            };
        }

        public RevisionCommand Internal(string action, Guid nodeId, ClusterOperation payload)
        {
            return ClusterLifecycleCommands.Create(Scenario.State, Scenario.Node, Storage, action, nodeId, payload, DateTimeOffset.UnixEpoch);
        }
    }
}
