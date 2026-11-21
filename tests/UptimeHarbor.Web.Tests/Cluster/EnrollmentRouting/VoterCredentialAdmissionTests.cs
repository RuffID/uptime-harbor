using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class VoterCredentialAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeaderAcceptsAuthorizeAndBindAndReplaysOriginalDecisionsAndDer(bool localLeader)
    {
        await using VoterCredentialPeerFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await InitializeAsync(fixture, localLeader, token);
        VoterCredentialAuthorizeCall authorize = await CreateAuthorizeAsync(fixture, token);
        CommandResult authorization = await fixture.Subject.RotationClient.AuthorizeAsync(authorize, token);
        Assert.Equal(OperationOutcome.Committed, authorization.Outcome);
        Assert.Equal(authorization, await fixture.Subject.RotationClient.AuthorizeAsync(authorize, token));

        VoterCredentialBindCall bind = await CreateBindAsync(fixture, authorize, authorization, token);
        EnrollmentIssuedArtifact repeatedArtifact = await fixture.Subject.VoterRouter.IssueAsync(bind.Signing, token);
        Assert.Equal(bind.Artifact.CertificateDer, repeatedArtifact.CertificateDer);
        Assert.Equal(bind.Artifact.IssueId, repeatedArtifact.IssueId);

        if (!localLeader)
        {
            fixture.Cluster.LoseResponse = EnrollmentPeerProtocol.ROTATION_BIND;
            await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Subject.RotationClient.BindAsync(bind, token));
        }
        else
        {
            await fixture.Subject.RotationClient.BindAsync(bind, token);
        }

        RevisionImage bound = fixture.Cluster.State.Current!;
        CommandResult expected = bound.Results[bind.Command.OperationId];
        Assert.Equal(OperationOutcome.Committed, expected.Outcome);
        fixture.Cluster.Term++;
        fixture.Cluster.SetLeader(localLeader ? fixture.Cluster.Nodes[1] : fixture.Subject);
        Assert.Equal(expected, await fixture.Subject.RotationClient.BindAsync(bind, token));
        Assert.Equal(authorization, await fixture.Subject.RotationClient.AuthorizeAsync(authorize, token));
        Assert.Same(bound, fixture.Cluster.State.Current);
        Assert.Equal(Convert.ToBase64String(bind.Artifact.CertificateDer), bound.Operations.Credentials[fixture.IssueId].PublicCertificate);
        Assert.Equal(expected.Index, bound.OnboardingContracts!.VoterCredentialRotations![fixture.IssueId].CredentialRevision);
        Assert.Contains(fixture.Cluster.Sent, call => call.Path == EnrollmentPeerProtocol.ROTATION_BIND);
    }

    [Fact]
    public async Task LostExpiredAuthorizeReturnsDurableRejectionAfterLeaderChange()
    {
        await using VoterCredentialPeerFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await InitializeAsync(fixture, false, token);
        fixture.Cluster.First.Time.Now += TimeSpan.FromSeconds(11);
        VoterCredentialAuthorizeCall call = await CreateAuthorizeAsync(fixture, token);
        fixture.Cluster.LoseResponse = EnrollmentPeerProtocol.ROTATION_AUTHORIZE;
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Subject.RotationClient.AuthorizeAsync(call, token));

        RevisionImage rejected = fixture.Cluster.State.Current!;
        CommandResult decision = rejected.Results[call.Command.OperationId];
        Assert.Equal(OperationOutcome.Rejected, decision.Outcome);
        Assert.Equal(RevisionCodec.Digest(call.Command), decision.Digest);
        Assert.Equal(rejected.Index, decision.Index);
        Assert.Null(rejected.OnboardingContracts!.VoterCredentialRotations?.GetValueOrDefault(fixture.IssueId));

        fixture.Cluster.Term++;
        fixture.Cluster.SetLeader(fixture.Subject);
        Assert.Equal(decision, await fixture.Subject.RotationClient.AuthorizeAsync(call, token));
        Assert.Same(rejected, fixture.Cluster.State.Current);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("storage")]
    [InlineData("client-leaf")]
    [InlineData("server-leaf")]
    [InlineData("origin")]
    [InlineData("proof")]
    [InlineData("process")]
    [InlineData("csr")]
    [InlineData("revision")]
    [InlineData("stamp")]
    [InlineData("decision-conflict")]
    [InlineData("nonce-term")]
    [InlineData("nonce-leader")]
    [InlineData("majority")]
    [InlineData("recovery")]
    [InlineData("leader-change")]
    [InlineData("expiry")]
    [InlineData("revocation")]
    [InlineData("proxy")]
    [InlineData("disabled")]
    public async Task AuthorizeCannotWriteWithChangedEvidence(string change)
    {
        await using VoterCredentialPeerFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await InitializeAsync(fixture, false, token);
        VoterCredentialAuthorizeCall call = await CreateAuthorizeAsync(fixture, token);
        EnrollmentRoutingFixture cluster = fixture.Cluster;
        EnrollmentRoutingNode leader = cluster.Leader;
        Guid oldId = call.ProofRequest.Request.Renewal.PreviousCredentialId;
        RevisionImage image = cluster.State.Current!;
        switch (change)
        {
            case "identity":
                cluster.EditRenewalRequest = peer => peer with { FromNodeId = fixture.Issuer.Options.NodeId };
                break;
            case "storage":
                cluster.EditRenewalRequest = peer => peer with { StorageGeneration = Guid.NewGuid() };
                break;
            case "client-leaf":
                cluster.RenewalIngressLeaf = fixture.Issuer.Certificate;
                break;
            case "server-leaf":
                cluster.RenewalServerLeaf = fixture.Issuer.Certificate;
                break;
            case "origin":
                call = call with { Command = call.Command with { OriginNodeId = fixture.Issuer.Options.NodeId } };
                break;
            case "proof":
                call = call with { ProofRequest = call.ProofRequest with { Proof = call.ProofRequest.Proof with { Signature = "not-a-signature" } } };
                break;
            case "process":
                OnlineProcessLease process = image.OnboardingContracts!.OnlineProcesses![fixture.Subject.Options.NodeId];
                fixture.Publish(image with { OnboardingContracts = image.OnboardingContracts with
                {
                    OnlineProcesses = image.OnboardingContracts.OnlineProcesses.SetItem(process.NodeId, process with { ProcessEpoch = 2 })
                } });
                break;
            case "csr":
                call = call with { ProofRequest = call.ProofRequest with { CsrDer = Convert.ToBase64String([1, 2, 3]) } };
                break;
            case "revision":
                call = call with { Command = call.Command with { BaseRevision = 1 } };
                break;
            case "stamp":
                call = call with { Command = call.Command with
                {
                    EnrollmentTrust = call.Command.EnrollmentTrust! with { CoordinatorAtUtc = cluster.First.Time.Now, CoordinatorTerm = 1 }
                } };
                break;
            case "decision-conflict":
                CommandResult conflict = new(call.Command.OperationId, new string('F', 64), OperationOutcome.Rejected,
                    image.Index, image.Term, image.Index, "TestDifferentCommand");
                image = image with { Results = image.Results.Add(conflict.OperationId, conflict) };
                fixture.Publish(image);
                break;
            case "nonce-term":
                cluster.Term++;
                cluster.Refresh();
                break;
            case "nonce-leader":
                EnableBootstrap(fixture.Issuer);
                cluster.Term++;
                cluster.SetLeader(fixture.Issuer);
                break;
            case "majority":
                leader.Native.OnBarrier = () => throw new InvalidOperationException("TestMajorityUnavailable");
                break;
            case "recovery":
                leader.Native.OnBarrier = cluster.State.BeginRecovery;
                break;
            case "leader-change":
                leader.Native.OnBarrier = () => cluster.SetLeader(fixture.Subject);
                break;
            case "expiry":
            case "revocation":
                OperationalCredential old = image.Operations.Credentials[oldId];
                old = change == "expiry"
                    ? old with { ExpiresAtUtc = cluster.First.Time.Now }
                    : old with { RevokedAtUtc = cluster.First.Time.Now };
                fixture.Publish(image with { Operations = image.Operations with { Credentials = image.Operations.Credentials.SetItem(oldId, old) } });
                break;
            case "proxy":
                leader.Options.TlsBoundary = "NginxIsolatedHttp";
                break;
            case "disabled":
                cluster.RotationAdmissionEnabled = false;
                break;
        }

        Exception? failure = await Record.ExceptionAsync(() => fixture.Subject.RotationClient.AuthorizeAsync(call, token));
        if (change is "expiry" or "revocation")
        {
            Assert.IsType<UnauthorizedAccessException>(failure);
        }
        else
        {
            Assert.IsType<HttpRequestException>(failure);
        }

        Assert.Equal(image.Results.GetValueOrDefault(call.Command.OperationId),
            cluster.State.Current!.Results.GetValueOrDefault(call.Command.OperationId));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("artifact")]
    [InlineData("authorization")]
    [InlineData("bind-proof")]
    [InlineData("majority")]
    [InlineData("result")]
    public async Task BindChecksExactOperationArtifactAuthorizationAndAppliedResult(string change)
    {
        await using VoterCredentialPeerFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await InitializeAsync(fixture, false, token);
        VoterCredentialAuthorizeCall authorize = await CreateAuthorizeAsync(fixture, token);
        CommandResult authorization = await fixture.Subject.RotationClient.AuthorizeAsync(authorize, token);
        VoterCredentialBindCall bind = await CreateBindAsync(fixture, authorize, authorization, token);
        switch (change)
        {
            case "operation":
                bind = bind with { Command = bind.Command with { OperationId = Guid.NewGuid() } };
                break;
            case "artifact":
                bind = bind with { Artifact = bind.Artifact with { TemplateDigest = new string('F', 64) } };
                break;
            case "authorization":
                bind = bind with { Signing = bind.Signing with { AuthorizationIndex = authorization.Index + 1 } };
                break;
            case "bind-proof":
                bind = bind with { BindProof = bind.Signing.ProofRequest.Proof };
                break;
            case "majority":
                fixture.Cluster.Leader.Native.OnBarrier = () => throw new InvalidOperationException("TestMajorityUnavailable");
                break;
            case "result":
                fixture.Cluster.EditResponse = (path, bytes) => path == EnrollmentPeerProtocol.ROTATION_BIND
                    ? StrictOnboardingJson.Encode(StrictOnboardingJson.Decode<CommandResult>(bytes) with { Index = authorization.Index })
                    : bytes;
                break;
        }

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Subject.RotationClient.BindAsync(bind, token));
        if (change == "result")
        {
            Assert.IsType<UnauthorizedAccessException>(failure);
            Assert.Equal(OperationOutcome.Committed, fixture.Cluster.State.Current!.Results[bind.Command.OperationId].Outcome);
        }
        else
        {
            Assert.IsType<HttpRequestException>(failure);
            Assert.False(fixture.Cluster.State.Current!.Operations.Credentials.ContainsKey(fixture.IssueId));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindReplayClosesAfterOldLeafRetirementOrOverlapExpiry(bool retire)
    {
        await using VoterCredentialPeerFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await InitializeAsync(fixture, true, token);
        VoterCredentialAuthorizeCall authorize = await CreateAuthorizeAsync(fixture, token);
        CommandResult authorization = await fixture.Subject.RotationClient.AuthorizeAsync(authorize, token);
        VoterCredentialBindCall bind = await CreateBindAsync(fixture, authorize, authorization, token);
        await fixture.Subject.RotationClient.BindAsync(bind, token);
        RevisionImage bound = fixture.Cluster.State.Current!;
        VoterCredentialRotation rotation = bound.OnboardingContracts!.VoterCredentialRotations![fixture.IssueId];
        if (retire)
        {
            fixture.Publish(bound with { OnboardingContracts = bound.OnboardingContracts with
            {
                VoterCredentialRotations = bound.OnboardingContracts.VoterCredentialRotations.SetItem(fixture.IssueId,
                    rotation with { RetiredIndex = bound.Index + 1, RetiredAtUtc = fixture.Cluster.First.Time.Now })
            } });
        }
        else
        {
            fixture.Cluster.First.Time.Now = rotation.OverlapUntilUtc!.Value;
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Subject.RotationClient.BindAsync(bind, token));
    }

    private static async Task InitializeAsync(VoterCredentialPeerFixture fixture, bool localLeader, CancellationToken token)
    {
        fixture.Cluster.UseManagedPools = true;
        await fixture.InitializeAsync(token);
        fixture.Cluster.RotationAdmissionEnabled = true;
        fixture.Publish(fixture.BeforeAuthorization);
        EnableBootstrap(fixture.Cluster.Leader);
        EnableBootstrap(fixture.Subject);
        if (localLeader)
        {
            fixture.Cluster.SetLeader(fixture.Subject);
        }
    }

    private static void EnableBootstrap(EnrollmentRoutingNode node)
    {
        var profile = node.Installation.Current! with { Mode = "Bootstrap", ProfileRevision = node.Installation.Current!.ProfileRevision + 1 };
        node.Installation.Publish(profile);
    }

    private static async Task<VoterCredentialAuthorizeCall> CreateAuthorizeAsync(VoterCredentialPeerFixture fixture, CancellationToken token)
    {
        VoterCredentialProofRequest input = fixture.Call.ProofRequest;
        VoterCredentialProof draft = input.Proof with { Nonce = string.Empty, Signature = string.Empty, IssuedAtUtc = fixture.Cluster.First.Time.Now };
        string nonce = await fixture.Subject.RotationClient.IssueNonceAsync(new(input.Request, draft), token);
        using ECDsa key = fixture.Subject.Certificate.GetECDsaPrivateKey()!;
        VoterCredentialProof proof = VoterCredentialProofs.Sign(input.Request, draft with { Nonce = nonce }, key);
        return new(VoterCredentialAuthorizationCommand.Create(input.Request), input with { Proof = proof });
    }

    private static async Task<VoterCredentialBindCall> CreateBindAsync(VoterCredentialPeerFixture fixture,
        VoterCredentialAuthorizeCall authorization, CommandResult decision, CancellationToken token)
    {
        VoterCredentialSigningRequest signing = new(1, authorization.ProofRequest, decision.Index);
        EnrollmentIssuedArtifact artifact = await fixture.Subject.VoterRouter.IssueAsync(signing, token);
        RevisionCommand command = VoterCredentialBindCommand.Create(signing.ProofRequest.Request, artifact, Guid.NewGuid());
        using ECDsa key = fixture.Subject.Certificate.GetECDsaPrivateKey()!;
        return new(command, signing, artifact, VoterCredentialProofs.SignBind(signing, command, signing.ProofRequest.Proof, key));
    }
}
