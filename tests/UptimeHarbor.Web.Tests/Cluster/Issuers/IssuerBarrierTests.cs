using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.Issuers;

public class IssuerBarrierTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("unsupported")]
    [InlineData("term")]
    [InlineData("membership")]
    [InlineData("leader")]
    [InlineData("identity")]
    [InlineData("boot")]
    [InlineData("schema")]
    [InlineData("cluster")]
    [InlineData("challenge")]
    [InlineData("credential")]
    [InlineData("oversize")]
    [InlineData("majority")]
    [InlineData("follower")]
    [InlineData("native-voters")]
    public async Task UnsupportedOrUnconfirmedVoterDeniesBeforeLocalKeyOrPending(string mutation)
    {
        // Arrange
        using IssuerRegistrationFixture fixture = new();
        switch (mutation)
        {
            case "missing":
                fixture.Peer.Edit = health => health with { EnrollmentTrustVersion = 0 };
                break;
            case "unsupported":
                fixture.Peer.Edit = health => health with { EnrollmentTrustVersion = 2 };
                break;
            case "term":
                fixture.Peer.Edit = health => health with { Term = health.Term + 1 };
                break;
            case "membership":
                fixture.Peer.Edit = health => health with { MembershipVersion = health.MembershipVersion + 1 };
                break;
            case "leader":
                fixture.Peer.Edit = health => health with { LeaderNodeId = Guid.NewGuid() };
                break;
            case "identity":
                fixture.Peer.Edit = health => health with { NodeId = Guid.NewGuid() };
                break;
            case "boot":
                fixture.Peer.Edit = health => health with { BootId = Guid.Empty };
                break;
            case "schema":
                fixture.Peer.EditEnvelope = reply => reply with { Compatibility = "2.0/2/2/2/1/6.10.0" };
                break;
            case "cluster":
                fixture.Peer.EditEnvelope = reply => reply with { ClusterId = Guid.NewGuid() };
                break;
            case "challenge":
                fixture.Peer.EditEnvelope = reply => reply with { Challenge = Guid.NewGuid() };
                break;
            case "credential":
                fixture.Peer.EditEnvelope = reply => reply with { CredentialId = Guid.NewGuid() };
                break;
            case "oversize":
                fixture.Peer.Oversize = true;
                break;
            case "majority":
                fixture.Native.FailAt = 1;
                break;
            case "follower":
                fixture.Runtime.Consensus = fixture.Runtime.Consensus! with { IsLeader = false };
                break;
            case "native-voters":
                fixture.Runtime.Consensus = fixture.Runtime.Consensus! with { VotingNodes = new HashSet<Guid> { fixture.Options.NodeId } };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        // Act
        JsonResult result = await fixture.Controller.RegisterAsync(new(Guid.NewGuid(), "test-password"), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(409, result.StatusCode);
        Assert.False(Directory.Exists(fixture.Local.KeyDirectory));
        Assert.Empty(fixture.Signing.Issuers);
        Assert.Empty(fixture.Work.Items);
        Assert.Empty(fixture.Projection.Writes);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Theory]
    [InlineData("term")]
    [InlineData("membership")]
    [InlineData("address")]
    [InlineData("storage")]
    public async Task ChangeDuringVoterProofInvalidatesTheWholeBarrier(string mutation)
    {
        // Arrange
        using IssuerRegistrationFixture fixture = new();
        fixture.Peer.BeforeResponse = () =>
        {
            if (mutation == "term")
            {
                fixture.Runtime.Consensus = fixture.Runtime.Consensus! with { Term = 2 };
            }
            else if (mutation == "membership")
            {
                fixture.Runtime.Consensus = fixture.Runtime.Consensus! with { MembershipVersion = 2 };
            }
            else
            {
                RevisionImage image = fixture.State.Current!;
                OperationalNode node = image.Operations.Nodes[fixture.Remote];
                if (mutation == "address")
                {
                    node = node with { Address = "https://moved.invalid/internal/raft" };
                }
                else
                {
                    node = node with { StorageGeneration = Guid.NewGuid() };
                }

                fixture.State.Publish(image with { Operations = image.Operations with { Nodes = image.Operations.Nodes.SetItem(fixture.Remote, node) } });
            }
        };

        // Act
        Exception? failure = await Record.ExceptionAsync(async () => await fixture.Barrier.ConfirmAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
        Assert.False(Directory.Exists(fixture.Local.KeyDirectory));
    }

    [Fact]
    public async Task PeerBatchCannotBypassAllVoterCapabilityAfterPendingWasPrepared()
    {
        // Arrange
        using IssuerRegistrationFixture fixture = new();
        fixture.Delivery.Offline = true;
        Guid id = Guid.NewGuid();
        await fixture.Controller.RegisterAsync(new(id, "test-password"), TestContext.Current.CancellationToken);
        RevisionCommand command = RevisionCodec.Decode(fixture.Payloads.Unprotect(fixture.Work.Items[id].ProtectedPayload));
        fixture.Peer.Edit = health => health with { EnrollmentTrustVersion = 0 };

        // Act
        Exception? failure = await Record.ExceptionAsync(() => fixture.Dispatcher.AcceptAsync(command, fixture.Options.NodeId, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
        Assert.False(fixture.State.Current!.Results.ContainsKey(id));
        Assert.Empty(fixture.Projection.Writes);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task HealthyMajorityDoesNotReplaceCapabilityOfTheRemainingVoter()
    {
        // Arrange
        using IssuerRegistrationFixture fixture = new();
        Guid third = Guid.NewGuid();
        OperationalNode node = new(third, Guid.NewGuid(), "https://third.invalid/internal/raft", "third", true, true, false, 1);
        RevisionImage image = fixture.State.Current!;
        fixture.State.Publish(image with { Operations = image.Operations with { Nodes = image.Operations.Nodes.Add(third, node) } });
        fixture.Runtime.Consensus = fixture.Runtime.Consensus! with { VotingNodes = new HashSet<Guid> { fixture.Options.NodeId, fixture.Remote, third } };
        fixture.VoterCredentials.Add(new(Guid.NewGuid(), third, node.StorageGeneration, new(node.Address), new string('E', 64), true, false, fixture.Time.Now.AddDays(1)));
        fixture.Peer.Edit = health =>
        {
            if (health.NodeId == third)
            {
                return health with { EnrollmentTrustVersion = 0 };
            }

            return health;
        };

        // Act
        JsonResult rejected = await fixture.Controller.RegisterAsync(new(Guid.NewGuid(), "test-password"), TestContext.Current.CancellationToken);
        fixture.Peer.Edit = null;
        JsonResult accepted = await fixture.Controller.RegisterAsync(new(Guid.NewGuid(), "test-password"), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(409, rejected.StatusCode);
        Assert.Equal(200, accepted.StatusCode);
        Assert.Contains(third, fixture.Peer.Contacted);
        Assert.Contains(fixture.Remote, fixture.Peer.Contacted);
        Assert.Single(fixture.Signing.Issuers);
        Assert.Single(fixture.Projection.Writes);
    }

    [Fact]
    public void OldHealthWithoutCapabilityIsClosedByDefault()
    {
        // Arrange
        string oldHealth = "{\"nodeId\":\"" + Guid.NewGuid() + "\",\"components\":{}}";

        // Act
        PeerHealthSnapshot health = JsonSerializer.Deserialize<PeerHealthSnapshot>(oldHealth, JsonSerializerOptions.Web)!;

        // Assert
        Assert.Equal(0, health.EnrollmentTrustVersion);
    }
}
