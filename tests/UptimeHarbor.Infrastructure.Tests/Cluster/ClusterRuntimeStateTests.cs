using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class ClusterRuntimeStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactPendingNativeTransitionKeepsExistingAdministratorAccessButCannotOpenSender(bool foreignVoter)
    {
        // Arrange
        using ClusterControlFixture fixture = new();
        RevisionImage image = Image(fixture);
        Guid joining = Guid.NewGuid();
        Guid changeId = Guid.NewGuid();
        OperationalNode local = image.Operations.Nodes[fixture.Options.NodeId] with { Activated = true };
        image = image with { Operations = image.Operations with
        {
            Nodes = image.Operations.Nodes.SetItem(local.NodeId, local).Add(joining,
                new(joining, Guid.NewGuid(), "https://joining.invalid/internal/raft", "joining", false, false, false, 2)),
            Changes = image.Operations.Changes.Add(changeId, new(changeId, Guid.NewGuid(), 1, joining, false, 0, null, 2))
        } };
        RevisionStateStore revisions = new();
        revisions.Publish(image);
        Recovery recovered = new(image.Index, 1);
        ClusterRuntimeState state = new(recovered, revisions, fixture.Control, Options.Create(fixture.Options), fixture.Time);
        HashSet<Guid> native = [local.NodeId, joining];
        if (foreignVoter) native.Add(Guid.NewGuid());
        ConsensusStatus status = new(image.ClusterId, image.Generation, local.NodeId, image.Term, 1, true, image.Index,
            native, CancellationToken.None, CancellationToken.None);
        state.Publish(status);
        foreach (string component in new[] { "Database", "Projection", "Storage", "Clock", "Monitoring", "Telegram" })
            fixture.Readiness.Report(component, true, "Ready", TimeSpan.FromHours(1));
        fixture.Readiness.ConfirmRecovery(fixture.Readiness.RecoveryVersion, recovered.Accepted, status);
        fixture.Sign("enable.json", fixture.Request("Enable") with { OperationsRevision = image.Operations.Revision });

        // Act
        bool canWork = fixture.Readiness.CanWork(state);
        bool canSend = fixture.Readiness.CanSend(state);

        // Assert
        Assert.Equal(!foreignVoter, canWork);
        Assert.False(canSend);
        Assert.True(state.Accepted.MembershipChangePending);
    }

    [Fact]
    public void ActualPrerequisitesAndFreshManualGrantAreRequiredAfterAcceptedRecovery()
    {
        // Arrange
        using ClusterControlFixture fixture = new();
        RevisionStateStore revisions = new();
        RevisionImage image = Image(fixture);
        revisions.Publish(image);
        Recovery recovered = new(image.Index, 0);
        ClusterRuntimeState state = new(recovered, revisions, fixture.Control, Options.Create(fixture.Options), fixture.Time);
        ConsensusStatus status = new(image.ClusterId, image.Generation, fixture.Options.NodeId, image.Term, 0, true, image.Index,
            new HashSet<Guid> { fixture.Options.NodeId }, CancellationToken.None, CancellationToken.None);
        state.Publish(status);
        foreach (string component in new[] { "Database", "Projection", "Storage", "Clock", "Monitoring", "Telegram" })
        {
            fixture.Readiness.Report(component, true, "Ready", TimeSpan.FromHours(1));
        }

        fixture.Readiness.ConfirmRecovery(fixture.Readiness.RecoveryVersion, recovered.Accepted, status);

        // Act
        bool recoveryOnly = fixture.Readiness.CanWork(state);
        revisions.Publish(image with { Operations = image.Operations with
        {
            Nodes = image.Operations.Nodes.SetItem(fixture.Options.NodeId, image.Operations.Nodes[fixture.Options.NodeId] with { Activated = true })
        } });
        bool activationOnly = fixture.Readiness.CanWork(state);
        fixture.Sign("enable.json", fixture.Request("Enable") with { OperationsRevision = image.Operations.Revision });
        bool enabled = fixture.Readiness.CanWork(state);
        bool noSenderEpoch = fixture.Readiness.CanSend(state);
        SenderAuthorityKey sender = new(image.ClusterId, image.Generation, 0, image.Term, fixture.Options.NodeId, Guid.NewGuid());
        revisions.Publish(revisions.Current! with { Operations = revisions.Current!.Operations with { Sender = sender } });
        bool sendingReady = fixture.Readiness.CanSend(state);
        state.Publish(status with { ConsensusToken = new CancellationToken(true) });
        bool majorityLost = fixture.Readiness.CanSend(state);

        // Assert
        Assert.False(recoveryOnly);
        Assert.False(activationOnly);
        Assert.True(enabled);
        Assert.False(noSenderEpoch);
        Assert.True(sendingReady);
        Assert.False(majorityLost);
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("bot")]
    [InlineData("voters")]
    [InlineData("projection")]
    public void RevokedOrUnknownDependenciesKeepWorkClosed(string failure)
    {
        // Arrange
        using ClusterControlFixture fixture = new();
        RevisionImage image = Image(fixture);
        OperationalNode node = image.Operations.Nodes[fixture.Options.NodeId] with { Activated = true };
        image = image with { Operations = image.Operations with { Nodes = image.Operations.Nodes.SetItem(node.NodeId, node) } };
        if (failure == "credential")
        {
            OperationalCredential credential = image.Operations.Credentials[fixture.Options.CredentialId] with { RevokedAtUtc = fixture.Time.Now };
            image = image with { Operations = image.Operations with { Credentials = image.Operations.Credentials.SetItem(credential.Id, credential) } };
        }
        else if (failure == "bot")
        {
            fixture.Options.ExpectedBotId = 456;
        }

        RevisionStateStore revisions = new();
        revisions.Publish(image);
        if (failure == "projection")
        {
            revisions.Invalidate();
        }

        ClusterRuntimeState state = new(new Recovery(image.Index, 0), revisions, fixture.Control, Options.Create(fixture.Options), fixture.Time);
        HashSet<Guid> voters = [fixture.Options.NodeId];
        if (failure == "voters")
        {
            voters.Add(Guid.NewGuid());
        }

        state.Publish(new(image.ClusterId, image.Generation, fixture.Options.NodeId, image.Term, 0, true, image.Index,
            voters, CancellationToken.None, CancellationToken.None));
        fixture.Sign("enable.json", fixture.Request("Enable") with { OperationsRevision = image.Operations.Revision });

        // Act
        AcceptedClusterState result = state.Accepted;

        // Assert
        Assert.False(result.Activated);
        Assert.False(result.ManuallyEnabled);
    }

    private static RevisionImage Image(ClusterControlFixture fixture)
    {
        OperationalNode node = new(fixture.Options.NodeId, fixture.Options.StorageGeneration, fixture.Options.PublicUri, "first", true, false, false, 0);
        OperationalCredential credential = new(fixture.Options.CredentialId, node.NodeId, new string('A', 64), "public certificate",
            fixture.Time.Now.AddDays(1), null, null);
        return new(fixture.Options.ClusterId, fixture.Options.Generation)
        {
            Index = 2, Term = 1, Digest = new string('A', 64),
            Operations = new()
            {
                Nodes = new ClusterOperationsImage().Nodes.Add(node.NodeId, node),
                Credentials = new ClusterOperationsImage().Credentials.Add(credential.Id, credential), BotId = 123, Revision = 2
            }
        };
    }

    private class Recovery(long index, long membership) : IClusterRecovery
    {
        public ClusterSnapshot? Snapshot => null;
        public AcceptedClusterState Accepted => new(true, true, true, false, false, index, membership, Guid.Empty, false);

        public ValueTask<bool> RestoreAsync(Guid clusterId, long generation, Guid nodeId, Guid storageGeneration, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public ValueTask<bool> ConfirmLocalReplayAsync(long committedIndex, long appliedIndex, long membershipVersion, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public ValueTask<long> ApplyAsync(CommittedRecord entry, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public ValueTask ReclaimAsync(long watermark, CancellationToken token)
        {
            throw new NotSupportedException();
        }
    }
}
