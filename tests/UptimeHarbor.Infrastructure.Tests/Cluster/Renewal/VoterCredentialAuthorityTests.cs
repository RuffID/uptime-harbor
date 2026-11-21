using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

public class VoterCredentialAuthorityTests
{
    [Fact]
    public async Task PreparationRequiresOldKeyAndNewCsrWithoutAttemptOrAcceptedEffect()
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new();
        RevisionImage before = fixture.Subject.Current!;

        // Act
        RevisionCommand command = await fixture.PrepareAsync();
        Exception? replay = await Record.ExceptionAsync(async () => await fixture.SubjectAuthority.PrepareAuthorizationAsync(
            fixture.Call, fixture.SubjectId, fixture.SubjectStorage, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("AuthorizeVoterCredential", command.Action);
        Assert.Equal(fixture.Request.OperationId, command.OperationId);
        Assert.Equal(fixture.Request.Renewal, command.EnrollmentTrust!.Rotation!.Renewal);
        Assert.Null(command.EnrollmentTrust.CoordinatorAtUtc);
        Assert.Null(before.Onboarding);
        Assert.Same(before, fixture.Subject.Current);
        Assert.IsType<UnauthorizedAccessException>(replay);
        Assert.Empty(fixture.Journal.Issued);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("cluster")]
    [InlineData("generation")]
    [InlineData("operation")]
    [InlineData("node")]
    [InlineData("storage")]
    [InlineData("address")]
    [InlineData("previous-id")]
    [InlineData("fingerprint")]
    [InlineData("spki")]
    [InlineData("revision")]
    [InlineData("issue")]
    [InlineData("template")]
    [InlineData("epoch")]
    [InlineData("csr-digest")]
    [InlineData("same-key")]
    [InlineData("scope")]
    [InlineData("proof-version")]
    [InlineData("origin")]
    [InlineData("origin-storage")]
    [InlineData("boot")]
    [InlineData("challenge")]
    [InlineData("process")]
    [InlineData("recovery")]
    [InlineData("membership")]
    [InlineData("nonce")]
    [InlineData("signature")]
    [InlineData("csr")]
    [InlineData("csr-signature")]
    public async Task AlteredProofOrCsrCannotPrepareOrSign(string mutation)
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new();
        await fixture.AuthorizeAsync();
        VoterCredentialProofRequest call = fixture.Call;
        VoterCredentialRequest request = call.Request;
        VoterCredentialRenewal renewal = request.Renewal;
        EnrollmentIssuance leaf = renewal.Issuance;
        VoterCredentialProof proof = call.Proof;
        switch (mutation)
        {
            case "version":
                request = request with { Version = 2 };
                break;
            case "cluster":
                request = request with { ClusterId = Guid.NewGuid() };
                break;
            case "generation":
                request = request with { Generation = request.Generation + 1 };
                break;
            case "operation":
                request = request with { OperationId = Guid.NewGuid() };
                break;
            case "node":
                leaf = leaf with { NodeId = Guid.NewGuid() };
                break;
            case "storage":
                leaf = leaf with { StorageGeneration = Guid.NewGuid() };
                break;
            case "address":
                leaf = leaf with { PeerUri = "https://other.example.test/internal/raft" };
                break;
            case "previous-id":
                renewal = renewal with { PreviousCredentialId = Guid.NewGuid() };
                break;
            case "fingerprint":
                renewal = renewal with { PreviousFingerprint = new string('F', 64) };
                break;
            case "spki":
                renewal = renewal with { PreviousSpkiDigest = new string('F', 64) };
                break;
            case "revision":
                renewal = renewal with { PreviousCredentialRevision = 7 };
                break;
            case "issue":
                leaf = leaf with { IssueId = Guid.NewGuid() };
                break;
            case "template":
                leaf = leaf with { Serial = "1123456789ABCDEF0123456789ABCDEF" };
                break;
            case "epoch":
                leaf = leaf with { IssuerEpoch = Guid.NewGuid() };
                break;
            case "csr-digest":
                leaf = leaf with { CsrDigest = new string('F', 64) };
                break;
            case "same-key":
                leaf = leaf with { SpkiDigest = renewal.PreviousSpkiDigest };
                break;
            case "scope":
                proof = proof with { Scope = "ContinueCredential" };
                break;
            case "proof-version":
                proof = proof with { Version = 2 };
                break;
            case "origin":
                proof = proof with { OriginNodeId = Guid.NewGuid() };
                break;
            case "origin-storage":
                proof = proof with { OriginStorageGeneration = Guid.NewGuid() };
                break;
            case "boot":
                proof = proof with { BootId = Guid.NewGuid() };
                break;
            case "challenge":
                proof = proof with { BootChallenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
                break;
            case "process":
                proof = proof with { ProcessEpoch = proof.ProcessEpoch + 1 };
                break;
            case "recovery":
                proof = proof with { RecoveryVersion = proof.RecoveryVersion + 1 };
                break;
            case "membership":
                proof = proof with { MembershipVersion = proof.MembershipVersion + 1 };
                break;
            case "nonce":
                proof = proof with { Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
                break;
            case "signature":
                proof = proof with { Signature = Convert.ToBase64String(new byte[70]) };
                break;
            case "csr":
                call = call with { CsrDer = Convert.ToBase64String(new byte[32]) };
                break;
            case "csr-signature":
                byte[] csr = fixture.Scenario.Csr.ToArray();
                csr[^1] ^= 1;
                leaf = leaf with { CsrDigest = UptimeHarbor.Infrastructure.Cluster.Onboarding.EnrollmentCertificates.Hash(csr) };
                call = call with { CsrDer = Convert.ToBase64String(csr) };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        request = request with { Renewal = renewal with { Issuance = leaf } };
        call = call with { Request = request, Proof = proof };
        if (mutation is "template" or "csr-signature")
        {
            call = call with { Proof = VoterCredentialProofs.Sign(request, proof, fixture.Scenario.Crypto.PeerKey) };
        }

        // Act
        Exception? failure = await Record.ExceptionAsync(() => fixture.Signer().IssueAsync(
            fixture.SigningCall() with { ProofRequest = call }, fixture.SubjectId, fixture.SubjectStorage, TestContext.Current.CancellationToken));

        // Assert
        Assert.NotNull(failure);
        Assert.Empty(fixture.Journal.Issued);
        Assert.Equal(0, fixture.Journal.Commits);
        Assert.Single(fixture.Scenario.State.Operations.Credentials);
        Assert.All(fixture.Logs.Messages, item => Assert.NotEqual(Microsoft.Extensions.Logging.LogLevel.Information, item.Level));
    }

    [Fact]
    public void NewKeyCannotProvePossessionOfPreviousOperationalKey()
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new();

        // Act
        Exception? failure = Record.Exception(() => VoterCredentialProofs.Sign(fixture.Request, fixture.Call.Proof, fixture.Scenario.NewKey));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
    }

    [Fact]
    public async Task AuthenticatedCallerMustBeExactSubjectVoter()
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new(true);
        await fixture.AuthorizeAsync();

        // Act
        Exception? failure = await Record.ExceptionAsync(() => fixture.Signer().IssueAsync(fixture.SigningCall(),
            fixture.IssuerOptions.NodeId, fixture.IssuerOptions.StorageGeneration, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(failure);
        Assert.Empty(fixture.Journal.Issued);
    }

    [Fact]
    public async Task NoAcceptedAuthorizationCannotSignEvenWithValidOldProofAndCsr()
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new();

        // Act
        Exception? failure = await Record.ExceptionAsync(() => fixture.Router().IssueAsync(fixture.SigningCall(), TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(failure);
        Assert.Empty(fixture.Journal.Issued);
    }
}
