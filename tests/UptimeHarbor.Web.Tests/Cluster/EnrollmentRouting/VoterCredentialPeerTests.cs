using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Web.Cluster.EnrollmentRouting;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class VoterCredentialPeerTests
{
    [Fact]
    public async Task TypedBodyCannotBeSerializedAgainAfterFirstWrite()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        using MemoryStream first = new();
        using MemoryStream retry = new();
        fixture.Cluster.BeforeReadBody = async (content, cancellationToken) =>
        {
            await content.CopyToAsync(first, cancellationToken);
            await content.CopyToAsync(retry, cancellationToken);
        };

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.IssueAsync(token));

        // Assert
        Assert.True(first.Length > 0);
        Assert.Equal(0, retry.Length);
        Assert.Empty(fixture.Cluster.Sent);
        Assert.Equal(fixture.InitialIssueCount, fixture.Issuer.Store.Issues.Count);
    }

    [Fact]
    public async Task ReusedConnectionRejectsChangedAcceptedEvidenceAfterAuthenticatedResponse()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        fixture.Cluster.UseManagedPools = true;
        await fixture.InitializeAsync(token);
        await fixture.IssueAsync(token);
        await fixture.IssueAsync(token);
        Guid id = fixture.Call.ProofRequest.Request.OperationId;
        CommandResult result = fixture.Authorized.Results[id];
        fixture.Cluster.AfterSend = (_, _) => fixture.Publish(fixture.Authorized with
        {
            Results = fixture.Authorized.Results.SetItem(id, result with { AppliedRevision = result.AppliedRevision - 1 })
        });

        // Act
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.IssueAsync(token));

        // Assert
        Assert.Equal(2, fixture.Cluster.ManagedHandshakes);
        Assert.Equal(3, fixture.Cluster.Sent.Count);
        Assert.Equal(fixture.InitialIssueCount + 1, fixture.Issuer.Store.Issues.Count);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("previous")]
    [InlineData("missing")]
    public async Task AfterPublication_ReplyAndSignerFollowTheConnectionSnapshot(string connection)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        DynamicPeerTls.Snapshot old = Assert.IsType<DynamicPeerTls.Snapshot>(fixture.Issuer.Tls.Capture());
        X509Certificate2 replacement = fixture.BindIssuerReplacement();
        DynamicPeerTls.Snapshot published = fixture.PublishIssuerReplacement(replacement);
        if (connection == "previous")
        {
            fixture.Cluster.RenewalConnectionSnapshot = old;
        }
        else
        {
            fixture.Cluster.RenewalServerLeaf = replacement;
            if (connection == "new")
            {
                fixture.Cluster.RenewalConnectionSnapshot = published;
            }
        }

        // Act
        EnrollmentIssuedArtifact? artifact = null;
        Exception? failure = await Record.ExceptionAsync(async () => artifact = await fixture.IssueAsync(token));

        // Assert
        Assert.Equal(old.CredentialId, fixture.Issuer.Options.CredentialId);
        Assert.Same(published, fixture.Issuer.Tls.Capture());
        if (connection == "missing")
        {
            Assert.IsType<HttpRequestException>(failure);
            Assert.Equal(fixture.InitialIssueCount, fixture.Issuer.Store.Issues.Count);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal(fixture.IssueId, artifact!.IssueId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlapUsesThePresentedIssuerLeaf(bool replacementPresented)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        X509Certificate2 replacement = fixture.BindIssuerReplacement();
        if (replacementPresented)
        {
            fixture.Cluster.RenewalServerLeaf = replacement;
            fixture.Cluster.RenewalConnectionSnapshot = fixture.PublishIssuerReplacement(replacement);
        }
        else
        {
            replacement.Dispose();
        }

        // Act
        EnrollmentIssuedArtifact artifact = await fixture.IssueAsync(token);

        // Assert
        Assert.Equal(fixture.IssueId, artifact.IssueId);
        Assert.Single(fixture.Cluster.Sent);
    }

    [Theory]
    [InlineData("other-reply-id")]
    [InlineData("expired-overlap")]
    [InlineData("missing-current-binding")]
    public async Task ResponseNeedsExactUsableLeafForThisRequest(string scenario)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        DateTimeOffset overlapEnd = fixture.Cluster.First.Time.Now.AddSeconds(1);
        using X509Certificate2 replacement = fixture.BindIssuerReplacement(overlapEnd);
        await fixture.IssueAsync(token);
        if (scenario == "other-reply-id")
        {
            Guid other = fixture.Cluster.State.Current!.Operations.Credentials.Values.Single(item =>
                item.NodeId == fixture.Issuer.Options.NodeId && item.Id != fixture.Issuer.Options.CredentialId).Id;
            fixture.Cluster.EditRenewalReply = reply => reply with { CredentialId = other };
        }
        else if (scenario == "expired-overlap")
        {
            fixture.Cluster.AfterSend = (_, _) => fixture.Cluster.First.Time.Now = overlapEnd;
        }
        else
        {
            fixture.Cluster.SkipRenewalConnectionBinding = true;
        }

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.IssueAsync(token));

        // Assert
        Assert.Equal(2, fixture.Cluster.Sent.Count);
        Assert.Equal(fixture.InitialIssueCount + 1, fixture.Issuer.Store.Issues.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WrongClientLeafIsRejectedByTheCheckingEnd(bool outbound)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        if (outbound)
        {
            fixture.Cluster.RenewalClientLeaf = fixture.Cluster.Leader.Certificate;
        }
        else
        {
            fixture.Cluster.RenewalIngressLeaf = fixture.Cluster.Leader.Certificate;
        }

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.IssueAsync(token));

        // Assert
        Assert.Equal(outbound ? 0 : 1, fixture.Cluster.Sent.Count);
        Assert.Equal(fixture.InitialIssueCount, fixture.Issuer.Store.Issues.Count);
    }

    [Fact]
    public async Task CallerRechecksAcceptedResultAtBindingBeforeHttp()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        Guid operation = fixture.Call.ProofRequest.Request.OperationId;
        CommandResult result = fixture.Authorized.Results[operation];
        fixture.Cluster.BeforeRenewalBinding = () => fixture.Publish(fixture.Authorized with
        {
            Results = fixture.Authorized.Results.SetItem(operation, result with { AppliedRevision = result.AppliedRevision - 1 })
        });

        // Act
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.IssueAsync(token));

        // Assert
        Assert.Empty(fixture.Cluster.Sent);
        Assert.Equal(fixture.InitialIssueCount, fixture.Issuer.Store.Issues.Count);
    }

    [Fact]
    public async Task LostReplyAndRetryReturnSameDerFromIssuerWithoutBind()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        fixture.Cluster.LoseResponse = EnrollmentPeerProtocol.RENEW;

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.IssueAsync(token));
        byte[] persisted = fixture.Issuer.Store.Issues[fixture.IssueId].CertificateDer;
        EnrollmentIssuedArtifact replay = await fixture.Subject.VoterRouter.IssueAsync(fixture.Call, token);

        // Assert
        Assert.Equal(persisted, replay.CertificateDer);
        Assert.Equal(fixture.InitialIssueCount + 1, fixture.Issuer.Store.Issues.Count);
        Assert.Empty(fixture.Cluster.Leader.Store.Issues);
        Assert.Equal(2, fixture.Cluster.Sent.Count);
        Assert.All(fixture.Cluster.Sent, item => Assert.Equal(
            (fixture.Subject.Options.NodeId, fixture.Issuer.Options.NodeId, EnrollmentPeerProtocol.RENEW),
            (item.Source, item.Target, item.Path)));
        Assert.Equal(fixture.Cluster.Sent[0].Body, fixture.Cluster.Sent[1].Body);
        Assert.Same(fixture.Authorized, fixture.Cluster.State.Current);
        Assert.False(fixture.Authorized.Operations.Credentials.ContainsKey(fixture.IssueId));
    }

    [Fact]
    public async Task NoProductionHandlerOrAuthenticatedEnvelopeReturnsFailure()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        fixture.Cluster.RenewalEnabled = false;
        EnrollmentPeerController isolated = new(fixture.Issuer.Service, fixture.Issuer.Limits, fixture.Issuer.Log, fixture.Issuer.Renewal)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() }
        };

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.IssueAsync(token));
        IActionResult anonymous = await isolated.RenewAsync(token);

        // Assert
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(anonymous).StatusCode);
        Assert.Equal(fixture.InitialIssueCount, fixture.Issuer.Store.Issues.Count);
    }

    [Fact]
    public async Task WrongIssuerAndWrongLocalCredentialAreRejectedBeforeTransport()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);

        // Act
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Subject.Client.IssueAsync(fixture.Cluster.Leader.Options.NodeId, fixture.Call, token));
        fixture.Subject.Options.CredentialId = Guid.NewGuid();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.IssueAsync(token));

        // Assert
        Assert.Empty(fixture.Cluster.Sent);
        Assert.Equal(fixture.InitialIssueCount, fixture.Issuer.Store.Issues.Count);
    }

    [Theory]
    [InlineData("term")]
    [InlineData("credential")]
    [InlineData("storage")]
    public async Task RequestWithChangedPeerAuthorityCannotReachJournal(string field)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        fixture.Cluster.EditRenewalRequest = peer => field switch
        {
            "term" => peer with { Term = peer.Term + 1 },
            "credential" => peer with { CredentialId = fixture.Cluster.Leader.Options.CredentialId },
            _ => peer with { StorageGeneration = Guid.NewGuid() }
        };

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.IssueAsync(token));

        // Assert
        Assert.Single(fixture.Cluster.Sent);
        Assert.Equal(fixture.InitialIssueCount, fixture.Issuer.Store.Issues.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IssuerRechecksAcceptedResultAfterClientValidation(bool changeOperationId)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        Guid id = fixture.Call.ProofRequest.Request.OperationId;
        CommandResult result = fixture.Authorized.Results[id];
        CommandResult altered = changeOperationId ? result with { OperationId = Guid.NewGuid() }
            : result with { AppliedRevision = result.AppliedRevision - 1 };
        fixture.Cluster.BeforeSend = (_, _) => fixture.Publish(fixture.Authorized with
        {
            Results = fixture.Authorized.Results.SetItem(id, altered)
        });

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.IssueAsync(token));

        // Assert
        Assert.Equal(fixture.InitialIssueCount, fixture.Issuer.Store.Issues.Count);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("index")]
    [InlineData("der")]
    public async Task RequesterRejectsChangedArtifact(string field)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        fixture.Cluster.EditResponse = (_, bytes) =>
        {
            EnrollmentIssuedArtifact artifact = StrictOnboardingJson.Decode<EnrollmentIssuedArtifact>(bytes);
            return StrictOnboardingJson.Encode(field switch
            {
                "operation" => artifact with { AttemptId = Guid.NewGuid() },
                "index" => artifact with { AuthorizationIndex = artifact.AuthorizationIndex + 1 },
                _ => artifact with { CertificateDer = fixture.Subject.Certificate.RawData }
            });
        };

        // Act
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.IssueAsync(token));

        // Assert
        Assert.Equal(fixture.InitialIssueCount + 1, fixture.Issuer.Store.Issues.Count);
        Assert.Same(fixture.Authorized, fixture.Cluster.State.Current);
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("challenge")]
    [InlineData("term")]
    public async Task RequesterRejectsChangedReplyEnvelope(string field)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        fixture.Cluster.EditRenewalReply = peer => field switch
        {
            "credential" => peer with { CredentialId = fixture.Cluster.Leader.Options.CredentialId },
            "challenge" => peer with { Challenge = Guid.NewGuid() },
            _ => peer with { Term = peer.Term + 1 }
        };

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.IssueAsync(token));

        // Assert
        Assert.Equal(fixture.InitialIssueCount + 1, fixture.Issuer.Store.Issues.Count);
        Assert.Same(fixture.Authorized, fixture.Cluster.State.Current);
    }

    [Fact]
    public async Task RequesterRejectsRevokedIssuerAfterReply()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        AcceptedEnrollmentIssuer issuer = fixture.Authorized.OnboardingContracts!.Issuers!.Values.Single();
        fixture.Cluster.AfterSend = (_, _) => fixture.Publish(fixture.Authorized with
        {
            OnboardingContracts = fixture.Authorized.OnboardingContracts with
            {
                Issuers = fixture.Authorized.OnboardingContracts.Issuers.SetItem(issuer.Epoch,
                    issuer with { RevokedAtUtc = fixture.Cluster.First.Time.Now })
            }
        });

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.IssueAsync(token));

        // Assert
        Assert.Equal(fixture.InitialIssueCount + 1, fixture.Issuer.Store.Issues.Count);
        Assert.False(fixture.Cluster.State.Current!.Operations.Credentials.ContainsKey(fixture.IssueId));
    }

    [Fact]
    public async Task RequesterRechecksAcceptedResultAfterAuthenticatedReply()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        Guid id = fixture.Call.ProofRequest.Request.OperationId;
        CommandResult result = fixture.Authorized.Results[id];
        fixture.Cluster.AfterSend = (_, _) => fixture.Publish(fixture.Authorized with
        {
            Results = fixture.Authorized.Results.SetItem(id, result with { AppliedRevision = result.AppliedRevision - 1 })
        });

        // Act
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.IssueAsync(token));

        // Assert
        Assert.Contains(fixture.IssueId, fixture.Issuer.Store.Issues.Keys);
        Assert.False(fixture.Cluster.State.Current!.Operations.Credentials.ContainsKey(fixture.IssueId));
    }

    [Fact]
    public async Task UnavailableIssuerAndLostRequesterMajorityNeverReturnArtifact()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        fixture.Cluster.OfflineSigner = true;

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.IssueAsync(token));
        Assert.Equal(fixture.InitialIssueCount, fixture.Issuer.Store.Issues.Count);
        fixture.Cluster.OfflineSigner = false;
        fixture.Cluster.AfterSend = (_, _) => fixture.Subject.Native.OnBarrier =
            () => throw new InvalidOperationException("TestMajorityUnavailable");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.IssueAsync(token));

        // Assert
        Assert.Equal(fixture.InitialIssueCount + 1, fixture.Issuer.Store.Issues.Count);
        Assert.Same(fixture.Authorized, fixture.Cluster.State.Current);
    }

    [Fact]
    public async Task IssuerMustConfirmMajorityAfterPersistingDerBeforeReturningIt()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        fixture.Issuer.Store.BeforeSaveChanges = _ =>
        {
            fixture.Issuer.Native.OnBarrier = () => throw new InvalidOperationException("TestMajorityUnavailable");
            return Task.CompletedTask;
        };

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.IssueAsync(token));

        // Assert
        Assert.Contains(fixture.IssueId, fixture.Issuer.Store.Issues.Keys);
        Assert.Equal(fixture.InitialIssueCount + 1, fixture.Issuer.Store.Issues.Count);
        Assert.Same(fixture.Authorized, fixture.Cluster.State.Current);
    }
}
