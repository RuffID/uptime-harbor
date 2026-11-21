using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

public class VoterCredentialReplayBoundaryTests
{
    [Theory]
    [InlineData("operation")]
    [InlineData("issue")]
    [InlineData("template")]
    public async Task ReSignedDifferentIntentCannotReuseAuthorizationOrJournal(string mutation)
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new(true);
        await fixture.AuthorizeAsync();
        EnrollmentIssuedArtifact original = await fixture.Router().IssueAsync(fixture.SigningCall(), TestContext.Current.CancellationToken);
        VoterCredentialRequest changed = fixture.Request;
        if (mutation == "operation")
        {
            changed = changed with { OperationId = Guid.NewGuid() };
        }
        else if (mutation == "issue")
        {
            changed = changed with { Renewal = changed.Renewal with { Issuance = changed.Renewal.Issuance with { IssueId = Guid.NewGuid() } } };
        }
        else
        {
            changed = changed with { Renewal = changed.Renewal with { Issuance = changed.Renewal.Issuance with { Serial = "1123456789ABCDEF0123456789ABCDEF" } } };
        }

        VoterCredentialProofRequest proof = fixture.Call with { Request = changed,
            Proof = VoterCredentialProofs.Sign(changed, fixture.Call.Proof, fixture.Scenario.Crypto.PeerKey) };

        // Act
        Exception? failure = await Record.ExceptionAsync(() => fixture.Router().IssueAsync(fixture.SigningCall() with { ProofRequest = proof },
            TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(failure);
        Assert.Equal(original.CertificateDer, Assert.Single(fixture.Journal.Issued).Value.CertificateDer);
        Assert.Equal(1, fixture.Journal.Commits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonceUsesMonotonicExpiryAndCannotBeReplacedBySignedRandomValue(bool expired)
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new();
        string nonce = await fixture.SubjectAuthority.IssueNonceAsync(fixture.Request, fixture.Call.Proof,
            fixture.SubjectId, fixture.SubjectStorage, TestContext.Current.CancellationToken);
        if (expired)
        {
            fixture.Time.Stamp += TimeSpan.FromSeconds(60).Ticks;
        }
        else
        {
            nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        }

        VoterCredentialProofRequest call = fixture.Call with { Proof = VoterCredentialProofs.Sign(fixture.Request,
            fixture.Call.Proof with { Nonce = nonce }, fixture.Scenario.Crypto.PeerKey) };

        // Act
        Exception? failure = await Record.ExceptionAsync(async () => await fixture.SubjectAuthority.PrepareAuthorizationAsync(call,
            fixture.SubjectId, fixture.SubjectStorage, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(failure);
        Assert.Empty(fixture.Journal.Issued);
        Assert.Null(fixture.Subject.Current!.OnboardingContracts!.VoterCredentialRotations);
    }

    [Fact]
    public async Task RecoveryDuringBarrierCannotUseItsStaleConfirmation()
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new();
        fixture.SubjectBarrier.After = _ => fixture.Subject.Invalidate();

        // Act
        Exception? failure = await Record.ExceptionAsync(async () => await fixture.SubjectAuthority.IssueNonceAsync(fixture.Request,
            fixture.Call.Proof, fixture.SubjectId, fixture.SubjectStorage, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(failure);
        Assert.Empty(fixture.Journal.Issued);
    }

    [Fact]
    public async Task NextRotationRequiresCurrentBoundRevisionAndCurrentKey()
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new();
        await fixture.AuthorizeAsync();
        EnrollmentIssuedArtifact first = await fixture.Router().IssueAsync(fixture.SigningCall(), TestContext.Current.CancellationToken);
        OperationalCredential next = new(first.IssueId, fixture.SubjectId, first.CertificateDigest, Convert.ToBase64String(first.CertificateDer),
            fixture.Request.Renewal.Issuance.NotAfterUtc, null, null);
        RevisionCommand bind = fixture.Scenario.Command("BindVoterCredential");
        bind = bind with { EnrollmentTrust = bind.EnrollmentTrust! with { Rotation = new(fixture.Request.Renewal, next, null) } };
        Assert.Equal(OperationOutcome.Committed, fixture.Scenario.Apply(bind).Outcome);
        long revision = fixture.Scenario.State.Index;
        fixture.Scenario.Commit("RetireOldCredential");
        fixture.Time.Now = fixture.Time.Now.AddDays(23);
        using ECDsa newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] csr = new CertificateRequest("CN=next rotation", newKey, HashAlgorithmName.SHA256).CreateSigningRequest();
        EnrollmentIssuance template = fixture.Request.Renewal.Issuance with
        {
            IssueId = Guid.NewGuid(), Serial = "1123456789ABCDEF0123456789ABCDEF",
            SpkiDigest = EnrollmentCertificates.Hash(newKey.ExportSubjectPublicKeyInfo()), CsrDigest = EnrollmentCertificates.Hash(csr),
            NotBeforeUtc = fixture.Time.Now.AddMinutes(-2), NotAfterUtc = fixture.Time.Now.AddDays(30)
        };
        fixture.Request = new(1, fixture.Request.ClusterId, fixture.Request.Generation, Guid.NewGuid(),
            new(next.Id, next.Fingerprint, fixture.Scenario.Intent.Issuance.SpkiDigest, revision, template));
        OnlineProcessLease process = fixture.Scenario.State.OnboardingContracts!.OnlineProcesses![fixture.SubjectId];
        fixture.Scenario.State = fixture.Scenario.State with { OnboardingContracts = fixture.Scenario.State.OnboardingContracts with
        {
            OnlineProcesses = fixture.Scenario.State.OnboardingContracts.OnlineProcesses.SetItem(process.NodeId,
                process with { LeaseUntilUtc = fixture.Time.Now.AddMinutes(10) })
        } };
        fixture.Publish();
        fixture.Call = fixture.Call with { Request = fixture.Request, CsrDer = Convert.ToBase64String(csr) };

        // Act
        await fixture.AuthorizeAsync(fixture.Scenario.NewKey);
        EnrollmentIssuedArtifact second = await fixture.Router().IssueAsync(fixture.SigningCall(), TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEqual(first.IssueId, second.IssueId);
        Assert.Equal(2, fixture.Journal.Commits);
        Assert.Equal(2, fixture.Subject.Current!.Operations.Credentials.Count);
        Assert.Equal(7, fixture.Subject.Current.Operations.MembershipVersion);
        Assert.Equal(revision, fixture.Request.Renewal.PreviousCredentialRevision);
        Assert.False(VoterCredentialPolicy.IsUsable(fixture.Subject.Current,
            fixture.Subject.Current.Operations.Credentials[fixture.Scenario.Intent.PreviousCredentialId], fixture.Time.Now));
    }
}
