using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class EnrollmentRoutingSecurityTests
{
    [Theory]
    [InlineData("origin")]
    [InlineData("storage")]
    [InlineData("proof")]
    [InlineData("cluster")]
    [InlineData("scope")]
    [InlineData("stamp")]
    public async Task CoordinatorRejectsForeignOriginStorageProofClusterScopeAndTimestamp(string mutation)
    {
        // Arrange
        await using EnrollmentRoutingFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(token);
        using EnrollmentRoutingFixture.JoiningKey key = await fixture.InviteAsync(token);
        await fixture.Ingress.Gateway.ExchangeAsync(key.Exchange(fixture.Ingress, "ReserveInvitation"), token);
        EnrollmentCoordinatedRequest call = StrictOnboardingJson.Decode<EnrollmentCoordinatedRequest>(fixture.Sent[0].Body);
        if (mutation == "origin")
        {
            call = call with { Command = call.Command with { OriginNodeId = fixture.Leader.Options.NodeId } };
        }
        else if (mutation == "storage")
        {
            call = call with { Command = call.Command with { Onboarding = call.Command.Onboarding! with { StorageGeneration = Guid.NewGuid() } } };
        }
        else if (mutation == "proof")
        {
            call = call with { Proof = call.Proof with { Signature = Convert.ToBase64String(new byte[70]) } };
        }
        else if (mutation == "cluster")
        {
            call = call with { Command = call.Command with { ClusterId = Guid.NewGuid() } };
        }
        else if (mutation == "scope")
        {
            call = call with { Command = call.Command with { Action = "CompleteOnboardingAttempt" }, Proof = call.Proof with { Action = "CompleteOnboardingAttempt" } };
        }
        else
        {
            call = call with { Command = call.Command with { Onboarding = call.Command.Onboarding! with { CoordinatorAtUtc = fixture.First.Time.Now, CoordinatorTerm = 1 } } };
        }

        int writes = fixture.Projection.Writes.Count;
        PeerEnvelope peer = Peer(fixture, fixture.Leader);

        // Act
        Exception? rejected = await Record.ExceptionAsync(() => fixture.Leader.Service.CoordinateAsync(call, peer, token));

        // Assert
        Assert.NotNull(rejected);
        Assert.Equal(writes, fixture.Projection.Writes.Count);
        Assert.Single(fixture.First.Store.Issues);
        Assert.Null(fixture.State.Current!.Onboarding!.Invitations[key.Request.InvitationId].Attempt!.Issuance);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("authorization")]
    [InlineData("serial")]
    [InlineData("epoch")]
    [InlineData("csr")]
    [InlineData("owner")]
    [InlineData("nonvoter")]
    [InlineData("majority")]
    public async Task SignerRejectsUnacceptedTemplateEpochCsrOriginAndMissingMajority(string mutation)
    {
        // Arrange
        await using EnrollmentRoutingFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(token);
        using EnrollmentRoutingFixture.JoiningKey key = await fixture.InviteAsync(token);
        await fixture.Ingress.Gateway.ExchangeAsync(key.Exchange(fixture.Ingress, "ReserveInvitation"), token);
        EnrollmentDescriptor issued = await fixture.Ingress.Gateway.ExchangeAsync(key.Exchange(fixture.Ingress, "AuthorizeEnrollmentCertificate"), token);
        EnrollmentSigningRequest call = StrictOnboardingJson.Decode<EnrollmentSigningRequest>(fixture.Sent.First(item => item.Path == EnrollmentPeerProtocol.ISSUE).Body);
        EnrollmentRoutingNode target = fixture.Issuer;
        if (mutation == "version")
        {
            call = call with { Version = 2 };
        }
        else if (mutation == "authorization")
        {
            call = call with { AuthorizationIndex = call.AuthorizationIndex + 1 };
        }
        else if (mutation == "serial")
        {
            call = call with { Issuance = call.Issuance with { Serial = new string('2', 32) } };
        }
        else if (mutation == "epoch")
        {
            call = call with { Issuance = call.Issuance with { IssuerEpoch = Guid.NewGuid() } };
        }
        else if (mutation == "csr")
        {
            call = call with { CsrDer = Convert.ToBase64String([1, 2, 3]) };
        }
        else if (mutation == "owner")
        {
            target = fixture.Leader;
        }
        else if (mutation == "nonvoter")
        {
            RevisionImage image = fixture.State.Current!;
            OperationalNode origin = image.Operations.Nodes[fixture.Ingress.Options.NodeId];
            fixture.State.Publish(image with { Operations = image.Operations with { Nodes = image.Operations.Nodes.SetItem(origin.NodeId, origin with { Voting = false }) } });
        }
        else
        {
            fixture.Issuer.Native.Majority = false;
        }

        int writes = fixture.Projection.Writes.Count;
        PeerEnvelope peer = Peer(fixture, target);

        // Act
        Exception? rejected = await Record.ExceptionAsync(() => target.Service.IssueAsync(call, peer, token));

        // Assert
        Assert.NotNull(rejected);
        Assert.Equal(writes, fixture.Projection.Writes.Count);
        Assert.Equal(Convert.FromBase64String(issued.CertificateDer!), fixture.First.Store.Issues[issued.Issuance!.IssueId].CertificateDer);
        Assert.Equal(2, fixture.First.Store.Issues.Count);
        Assert.Null(fixture.State.Current!.Onboarding!.Invitations[key.Request.InvitationId].Attempt!.CertificateDigest);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("template")]
    [InlineData("authorization")]
    [InlineData("der")]
    public async Task CallerRejectsAlteredSignerArtifactBeforeAnyBind(string mutation)
    {
        // Arrange
        await using EnrollmentRoutingFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(token);
        using EnrollmentRoutingFixture.JoiningKey key = await fixture.InviteAsync(token);
        await fixture.Ingress.Gateway.ExchangeAsync(key.Exchange(fixture.Ingress, "ReserveInvitation"), token);
        fixture.EditResponse = (path, bytes) =>
        {
            if (path != EnrollmentPeerProtocol.ISSUE)
            {
                return bytes;
            }

            EnrollmentIssuedArtifact artifact = StrictOnboardingJson.Decode<EnrollmentIssuedArtifact>(bytes);
            if (mutation == "digest")
            {
                artifact = artifact with { CertificateDigest = new string('F', 64) };
            }
            else if (mutation == "template")
            {
                artifact = artifact with { TemplateDigest = new string('F', 64) };
            }
            else if (mutation == "authorization")
            {
                artifact = artifact with { AuthorizationIndex = artifact.AuthorizationIndex + 1 };
            }
            else
            {
                artifact = artifact with { CertificateDer = [1, 2, 3] };
            }

            return StrictOnboardingJson.Encode(artifact);
        };

        // Act
        Exception? rejected = await Record.ExceptionAsync(() => fixture.Ingress.Gateway.ExchangeAsync(
            key.Exchange(fixture.Ingress, "AuthorizeEnrollmentCertificate"), token));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(rejected);
        Assert.False(fixture.State.Current!.Operations.Nodes.ContainsKey(key.Request.NodeId));
        Assert.Null(fixture.State.Current.Onboarding!.Invitations[key.Request.InvitationId].Attempt!.CertificateDigest);
        Assert.Equal(2, fixture.First.Store.Issues.Count);
    }

    [Fact]
    public async Task FollowerReadBarrierRejectsLeaderChangeDuringNativeRead()
    {
        // Arrange
        await using EnrollmentRoutingFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(token);
        fixture.Ingress.Native.OnBarrier = () => fixture.SetLeader(fixture.Issuer);

        // Act
        Exception? rejected = await Record.ExceptionAsync(async () => await fixture.Ingress.Barrier.ConfirmAsync(token));

        // Assert
        Assert.IsType<InvalidOperationException>(rejected);
        Assert.Single(fixture.First.Store.Issues);
        Assert.Empty(fixture.Sent);
    }

    internal static PeerEnvelope Peer(EnrollmentRoutingFixture fixture, EnrollmentRoutingNode target)
    {
        return new(fixture.State.Current!.ClusterId, 1, fixture.Ingress.Options.NodeId, target.Options.NodeId,
            fixture.Ingress.Options.StorageGeneration, fixture.Ingress.Options.CredentialId, Guid.NewGuid(),
            fixture.First.Time.Now, OnboardingProtocol.TRUST_COMPATIBILITY, Guid.NewGuid(), fixture.Term);
    }
}
