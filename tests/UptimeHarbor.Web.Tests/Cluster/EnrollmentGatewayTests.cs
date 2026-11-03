using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class EnrollmentGatewayTests
{
    [Fact]
    public async Task AcceptedReserveIssueBindAdmitPreserveDerAndNeverActivateNonvoter()
    {
        // Arrange
        await using ManagedBootstrapFixture first = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await ReadyAsync(first, token);
        using JoiningKey joining = new(first.Options);
        await InviteAsync(first, joining, token);

        // Act
        EnrollmentDescriptor reserved = await first.Enrollment.ExchangeAsync(joining.Exchange(first, "ReserveInvitation"), token);
        EnrollmentExchange issue = joining.Exchange(first, "AuthorizeEnrollmentCertificate");
        first.Native.LoseResponse = true;
        Exception? lost = await Record.ExceptionAsync(() => first.Enrollment.ExchangeAsync(issue, token));
        EnrollmentDescriptor issued = await first.Enrollment.ExchangeAsync(joining.Exchange(first, "AuthorizeEnrollmentCertificate", issue.Proof.RequestId), token);
        EnrollmentDescriptor retried = await first.Enrollment.ExchangeAsync(joining.Exchange(first, "AuthorizeEnrollmentCertificate", issue.Proof.RequestId), token);

        // Assert
        Assert.IsType<HttpRequestException>(lost);
        Assert.Equal(joining.Request, reserved.Request);
        Assert.True(reserved.ReservationIndex > 0);
        Assert.Equal(issued.CertificateDer, retried.CertificateDer);
        Assert.NotNull(issued.CertificateDer);
        Assert.DoesNotContain(first.Accepted.Current!.Operations.Credentials.Values, item => item.NodeId == joining.Request.NodeId);
        Assert.Equal(2, first.Store.Issues.Count);

        // Act
        EnrollmentDescriptor bound = await first.Enrollment.ExchangeAsync(joining.Exchange(first, "BindEnrollmentCredential"), token);
        EnrollmentDescriptor admitted = await first.Enrollment.ExchangeAsync(joining.Exchange(first, "AdmitInvitedNode"), token);
        PeerCredential peer = (await new AcceptedPeerTrustSource(first.Accepted, first.Time).ListAsync(token))
            .Single(item => item.NodeId == joining.Request.NodeId);

        // Assert
        Assert.Equal(issued.CertificateDer, bound.CertificateDer);
        Assert.Equal(bound.CertificateDer, admitted.CertificateDer);
        Assert.Equal(nameof(OnboardingAttemptStatus.Admitted), admitted.Status);
        Assert.True(peer.IsAdmission);
        Assert.False(peer.IsVoting);
        Assert.False(first.Accepted.Current!.Operations.Nodes[joining.Request.NodeId].Activated);
        Assert.Empty(first.Accepted.Current.Operations.Changes);
        Assert.Null(first.Accepted.Current.Operations.Sender);
        OnboardingContractSnapshot.Validate(first.Accepted.Current);

        // Act
        LocalAttemptStep saved = first.Store.Steps[issue.Proof.RequestId];
        RevisionCommand original = System.Text.Json.JsonSerializer.Deserialize<RevisionCommand>(saved.Evidence!)!;
        LocalCommandStepValidation.Require(first.Store.Profile!, saved, original, "Enrollment/");
        bool peerBypass = await first.EnrollmentAuthority.VerifyAsync(original, token);

        // Assert
        Assert.False(peerBypass);
    }

    [Fact]
    public async Task NonceReplayForeignIdentityAndRevokedDelegationDoNotIssueCertificates()
    {
        // Arrange
        await using ManagedBootstrapFixture first = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await ReadyAsync(first, token);
        using JoiningKey joining = new(first.Options);
        await InviteAsync(first, joining, token);
        EnrollmentExchange reserve = joining.Exchange(first, "ReserveInvitation");
        await first.Enrollment.ExchangeAsync(reserve, token);

        // Act
        Exception? replay = await Record.ExceptionAsync(() => first.Enrollment.ExchangeAsync(reserve, token));
        EnrollmentExchange foreign = joining.Exchange(first, "AuthorizeEnrollmentCertificate");
        foreign = foreign with { Request = foreign.Request with { NodeId = Guid.NewGuid() } };
        Exception? identity = await Record.ExceptionAsync(() => first.Enrollment.ExchangeAsync(foreign, token));
        RevisionImage image = first.Accepted.Current!;
        AcceptedValue actor = image.Values[first.Account.ActorUserId];
        first.Accepted.Publish(image with { Values = image.Values.SetItem(actor.Id, actor with { Revision = actor.Revision + 1 }) });
        Exception? delegation = await Record.ExceptionAsync(() => first.Enrollment.ExchangeAsync(joining.Exchange(first, "AuthorizeEnrollmentCertificate"), token));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(replay);
        Assert.IsType<UnauthorizedAccessException>(identity);
        Assert.IsType<UnauthorizedAccessException>(delegation);
        Assert.Single(first.Store.Issues);
        Assert.Single(first.Accepted.Current!.Operations.Nodes);
    }

    [Fact]
    public void ValidExchangesHaveSeparateGlobalAndRejectedSourceRateLimits()
    {
        // Arrange
        EnrollmentRateLimiter limiter = new(TimeProvider.System);

        // Act
        bool[] valid = Enumerable.Range(0, 10).Select(_ => limiter.TryAcquire("valid")).ToArray();
        foreach (int _ in Enumerable.Range(0, 5))
        {
            limiter.Rejected("invalid");
        }

        // Assert
        Assert.All(valid, Assert.True);
        Assert.False(limiter.TryAcquire("invalid"));
        Assert.True(limiter.TryAcquire("valid"));
    }

    internal static async Task ReadyAsync(ManagedBootstrapFixture first, CancellationToken token)
    {
        await first.StartQuarantineAsync(token);
        await first.AccountBootstrap.InitializeAsync(token);
        foreach (int _ in Enumerable.Range(0, 5))
        {
            await first.Driver.TickAsync(token);
        }
    }

    private static async Task InviteAsync(ManagedBootstrapFixture first, JoiningKey joining, CancellationToken token)
    {
        await InviteAsync(first, joining.Request.InvitationId, joining.Code, token);
    }

    internal static async Task InviteAsync(ManagedBootstrapFixture first, Guid invitationId, Guid code, CancellationToken token)
    {
        RevisionImage image = first.Accepted.Current!;
        RevisionCommand invitation = new()
        {
            Schema = 3, ClusterId = image.ClusterId, ClusterGeneration = image.Generation, OperationId = Guid.NewGuid(),
            OriginNodeId = first.Options.NodeId, ActorUserId = first.Account.ActorUserId,
            ActorRevision = image.Values[first.Account.ActorUserId].Revision, EntityId = invitationId,
            EntityKind = DeletedEntityKind.Node, Action = "IssueInvitation", BaseRevision = image.Onboarding?.Revision ?? 0,
            CreatedAtUtc = first.Time.Now, Onboarding = new()
            {
                StorageGeneration = first.Options.StorageGeneration, InvitationId = invitationId,
                Verifier = InvitationSecrets.Verifier(image.ClusterId, image.Generation, invitationId, code),
                ReauthenticatedAtUtc = first.Time.Now
            }
        };
        CommandResult result = await first.Dispatcher.AcceptAsync(invitation, first.Options.NodeId, token);
        Assert.Equal(OperationOutcome.Committed, result.Outcome);
    }

    private class JoiningKey : IDisposable
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly byte[] csr;
        public Guid Code { get; } = InvitationSecrets.CreateCode();
        public Guid Boot { get; } = Guid.NewGuid();
        public string Challenge { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public EnrollmentRequest Request { get; }

        public JoiningKey(ClusterOptions first)
        {
            csr = new CertificateRequest("CN=joining", key, HashAlgorithmName.SHA256).CreateSigningRequest();
            Request = new(1, first.ClusterId, first.Generation, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "https://joining.example.test:8443/internal/raft", EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()), EnrollmentCertificates.Hash(csr));
        }

        public EnrollmentExchange Exchange(ManagedBootstrapFixture first, string action, Guid? operation = null)
        {
            string digest = EnrollmentRequestCodec.Digest(Request);
            string nonce = first.Nonces.Issue(Request.AttemptId, Boot, Challenge, 1, action, digest);
            EnrollmentProof proof = new(1, operation ?? Guid.NewGuid(), action, Boot, Challenge, 1, nonce, digest, string.Empty);
            proof = EnrollmentProofs.Sign(Request, proof, key);
            string? code = null;
            if (action == "ReserveInvitation")
            {
                code = Code.ToString("D");
            }

            return new(1, Request, proof, Convert.ToBase64String(csr), code);
        }

        public void Dispose()
        {
            key.Dispose();
        }
    }
}
