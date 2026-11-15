using System.Text;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class RemoteEnrollmentTests
{
    [Fact]
    public async Task FollowerIngressCoordinatesWithRemoteLeaderAndFollowerSignerWithoutChangingBrowserOriginOrOpeningVoter()
    {
        // Arrange
        await using EnrollmentRoutingFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(token);
        using EnrollmentRoutingFixture.JoiningKey key = await fixture.InviteAsync(token);

        // Act
        EnrollmentDescriptor reserved = await fixture.Ingress.Gateway.ExchangeAsync(key.Exchange(fixture.Ingress, "ReserveInvitation"), token);
        EnrollmentDescriptor issued = await fixture.Ingress.Gateway.ExchangeAsync(key.Exchange(fixture.Ingress, "AuthorizeEnrollmentCertificate"), token);

        // Assert
        Assert.NotEqual(fixture.Leader.Options.NodeId, fixture.Issuer.Options.NodeId);
        Assert.NotEqual(fixture.Ingress.Options.NodeId, fixture.Leader.Options.NodeId);
        Assert.False(fixture.Issuer.View.Consensus!.IsLeader);
        Assert.False(fixture.Ingress.View.Consensus!.IsLeader);
        Assert.True(fixture.Ingress.View.Consensus.LeadershipToken.IsCancellationRequested);
        Assert.Equal(fixture.Ingress.Local.BrowserOrigin + "/", issued.Discovery.BrowserOrigin);
        Assert.Equal(fixture.Ingress.Options.PublicUri, issued.Discovery.PeerAddress);
        Assert.Equal(reserved.Request, issued.Request);
        Assert.NotNull(issued.CertificateDer);
        Assert.False(fixture.State.Current!.Operations.Nodes.ContainsKey(key.Request.NodeId));
        Assert.Equal(2, fixture.First.Store.Issues.Count);

        // Act
        EnrollmentDescriptor bound = await fixture.Ingress.Gateway.ExchangeAsync(key.Exchange(fixture.Ingress, "BindEnrollmentCredential"), token);
        EnrollmentDescriptor admitted = await fixture.Ingress.Gateway.ExchangeAsync(key.Exchange(fixture.Ingress, "AdmitInvitedNode"), token);
        EnrollmentState read = await fixture.Ingress.Gateway.StateAsync(key.Exchange(fixture.Ingress, "ReadEnrollmentAttempt"), token);

        // Assert
        Assert.Equal(issued.CertificateDer, bound.CertificateDer);
        Assert.Equal(issued.CertificateDer, admitted.CertificateDer);
        Assert.Equal("Admitted", read.Descriptor!.Status);
        Assert.Equal(fixture.Ingress.Local.BrowserOrigin + "/", read.Descriptor.Discovery.BrowserOrigin);
        OperationalNode joining = fixture.State.Current!.Operations.Nodes[key.Request.NodeId];
        Assert.False(joining.Voting);
        Assert.False(joining.Activated);
        Assert.Empty(fixture.State.Current.Operations.Changes);
        Assert.All(fixture.Ingress.Store.Steps.Values, step =>
        {
            RevisionCommand command = System.Text.Json.JsonSerializer.Deserialize<RevisionCommand>(step.Evidence!)!;
            Assert.Equal(fixture.Ingress.Options.NodeId, command.OriginNodeId);
        });
        Assert.All(fixture.Sent, call => Assert.DoesNotContain(key.Code.ToString("D"), Encoding.UTF8.GetString(call.Body)));
        Assert.Contains(fixture.Sent, call => call.Path == EnrollmentPeerProtocol.ISSUE && call.Target == fixture.Issuer.Options.NodeId);
        Assert.Contains(fixture.Sent, call => call.Path == EnrollmentPeerProtocol.COORDINATE && call.Target == fixture.Leader.Options.NodeId);
        Assert.Empty(fixture.Leader.Log.Entries);
        Assert.Empty(fixture.Issuer.Log.Entries);
    }

    [Theory]
    [InlineData(EnrollmentPeerProtocol.COORDINATE)]
    [InlineData(EnrollmentPeerProtocol.ISSUE)]
    public async Task LostAcceptedOrSignerResponseRetriesOriginalCommandIssueAndDerWithoutBinding(string boundary)
    {
        // Arrange
        await using EnrollmentRoutingFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(token);
        using EnrollmentRoutingFixture.JoiningKey key = await fixture.InviteAsync(token);
        await fixture.Ingress.Gateway.ExchangeAsync(key.Exchange(fixture.Ingress, "ReserveInvitation"), token);
        Guid operation = Guid.NewGuid();
        fixture.LoseResponse = boundary;

        // Act
        Exception? lost = await Record.ExceptionAsync(() => fixture.Ingress.Gateway.ExchangeAsync(
            key.Exchange(fixture.Ingress, "AuthorizeEnrollmentCertificate", operation), token));
        EnrollmentIssuance original = fixture.State.Current!.Onboarding!.Invitations[key.Request.InvitationId].Attempt!.Issuance!;
        byte[]? der = fixture.First.Store.Issues.GetValueOrDefault(operation)?.CertificateDer;
        string command = fixture.Ingress.Store.Steps[operation].Evidence!;
        EnrollmentDescriptor retry = await fixture.Ingress.Gateway.ExchangeAsync(
            key.Exchange(fixture.Ingress, "AuthorizeEnrollmentCertificate", operation), token);

        // Assert
        Assert.IsType<HttpRequestException>(lost);
        Assert.Equal(original, retry.Issuance);
        Assert.Equal(operation, retry.Issuance!.IssueId);
        Assert.Equal(command, fixture.Ingress.Store.Steps[operation].Evidence);
        Assert.Equal("CertificateAuthorized", retry.Status);
        Assert.Null(fixture.State.Current!.Onboarding!.Invitations[key.Request.InvitationId].Attempt!.CertificateDigest);
        Assert.False(fixture.State.Current.Operations.Nodes.ContainsKey(key.Request.NodeId));
        Assert.Equal(2, fixture.First.Store.Issues.Count);
        if (der is not null)
        {
            Assert.Equal(der, Convert.FromBase64String(retry.CertificateDer!));
        }

        Assert.Single(fixture.State.Current.Results.Values, result => result.OperationId == operation);
    }

    [Fact]
    public async Task SignerOutageLeavesAcceptedTemplateAndRetryNeverCreatesAnotherIssuerOrIdentity()
    {
        // Arrange
        await using EnrollmentRoutingFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(token);
        using EnrollmentRoutingFixture.JoiningKey key = await fixture.InviteAsync(token);
        await fixture.Ingress.Gateway.ExchangeAsync(key.Exchange(fixture.Ingress, "ReserveInvitation"), token);
        Guid operation = Guid.NewGuid();
        fixture.OfflineSigner = true;

        // Act
        Exception? offline = await Record.ExceptionAsync(() => fixture.Ingress.Gateway.ExchangeAsync(
            key.Exchange(fixture.Ingress, "AuthorizeEnrollmentCertificate", operation), token));
        EnrollmentIssuance original = fixture.State.Current!.Onboarding!.Invitations[key.Request.InvitationId].Attempt!.Issuance!;
        fixture.OfflineSigner = false;
        EnrollmentDescriptor retry = await fixture.Ingress.Gateway.ExchangeAsync(
            key.Exchange(fixture.Ingress, "AuthorizeEnrollmentCertificate", operation), token);

        // Assert
        Assert.IsType<HttpRequestException>(offline);
        Assert.Equal(original, retry.Issuance);
        Assert.Equal(key.Request, retry.Request);
        Assert.Single(fixture.State.Current!.OnboardingContracts!.Issuers!);
        Assert.Single(fixture.First.Store.Issuers);
        Assert.Equal(2, fixture.First.Store.Issues.Count);
        Assert.Empty(fixture.Ingress.Store.Issuers);
        Assert.Equal("CertificateAuthorized", retry.Status);
    }

    [Fact]
    public async Task LeaderChangeAfterDerPersistenceFailsCurrentCallAndRetryUsesSameDerOnNewLeader()
    {
        // Arrange
        await using EnrollmentRoutingFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(token);
        using EnrollmentRoutingFixture.JoiningKey key = await fixture.InviteAsync(token);
        await fixture.Ingress.Gateway.ExchangeAsync(key.Exchange(fixture.Ingress, "ReserveInvitation"), token);
        Guid operation = Guid.NewGuid();
        fixture.AfterSend = (_, path) =>
        {
            if (path == EnrollmentPeerProtocol.ISSUE)
            {
                fixture.AfterSend = null;
                fixture.Term++;
                fixture.SetLeader(fixture.Ingress);
            }
        };

        // Act
        Exception? changed = await Record.ExceptionAsync(() => fixture.Ingress.Gateway.ExchangeAsync(
            key.Exchange(fixture.Ingress, "AuthorizeEnrollmentCertificate", operation), token));
        byte[] der = fixture.First.Store.Issues[operation].CertificateDer;
        EnrollmentDescriptor retry = await fixture.Ingress.Gateway.ExchangeAsync(
            key.Exchange(fixture.Ingress, "AuthorizeEnrollmentCertificate", operation), token);

        // Assert
        Assert.IsType<InvalidOperationException>(changed);
        Assert.True(fixture.Ingress.View.Consensus!.IsLeader);
        Assert.Equal(der, Convert.FromBase64String(retry.CertificateDer!));
        Assert.Equal(operation, retry.Issuance!.IssueId);
        Assert.Equal(2, fixture.First.Store.Issues.Count);
        Assert.Null(fixture.State.Current!.Onboarding!.Invitations[key.Request.InvitationId].Attempt!.CertificateDigest);
    }

    [Fact]
    public async Task RemoteCommitCannotReturnDescriptorBeforeLocalAppliedBarrier()
    {
        // Arrange
        await using EnrollmentRoutingFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(token);
        using EnrollmentRoutingFixture.JoiningKey key = await fixture.InviteAsync(token);
        Guid operation = Guid.NewGuid();
        fixture.AfterSend = (_, _) => fixture.Ingress.Recovery.AppliedOverride = -1;

        // Act
        Exception? lagging = await Record.ExceptionAsync(() => fixture.Ingress.Gateway.ExchangeAsync(
            key.Exchange(fixture.Ingress, "ReserveInvitation", operation), token));
        int writes = fixture.Projection.Writes.Count;
        fixture.AfterSend = null;
        fixture.Ingress.Recovery.AppliedOverride = null;
        EnrollmentDescriptor retry = await fixture.Ingress.Gateway.ExchangeAsync(key.Exchange(fixture.Ingress, "ReserveInvitation", operation), token);

        // Assert
        Assert.IsType<InvalidOperationException>(lagging);
        Assert.True(retry.ReservationIndex > 0);
        Assert.Equal(writes, fixture.Projection.Writes.Count);
        Assert.Equal(operation, fixture.State.Current!.Onboarding!.Invitations[key.Request.InvitationId].Attempt!.ReserveOperationId);
    }
}
