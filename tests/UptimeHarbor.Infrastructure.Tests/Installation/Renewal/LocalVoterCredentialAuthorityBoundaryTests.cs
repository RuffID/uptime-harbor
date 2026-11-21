using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class LocalVoterCredentialAuthorityBoundaryTests
{
    [Theory]
    [InlineData("authorization", "majority")]
    [InlineData("authorization", "recovery")]
    [InlineData("authorization", "expiry")]
    [InlineData("artifact", "majority")]
    [InlineData("artifact", "recovery")]
    [InlineData("artifact", "expiry")]
    public async Task EachNewPin_RechecksFreshAuthorityAndDoesNotWriteOnFailure(string stage, string failure)
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        await fixture.PrepareAsync();
        await fixture.Authority.AuthorizeAsync();
        if (stage == "artifact")
        {
            await fixture.Store.PinAuthorizationAsync(fixture.Reference, fixture.Authority.SigningCall(), fixture.Token);
            await fixture.SignAsync();
        }

        int logs = fixture.Logs.Messages.Count;
        if (failure == "majority")
        {
            fixture.Authority.SubjectBarrier.Available = false;
        }
        else if (failure == "recovery")
        {
            fixture.Authority.SubjectBarrier.After = _ => fixture.Authority.Subject.Invalidate();
        }
        else
        {
            fixture.Authority.Time.Now = fixture.Authority.Time.Now.AddMinutes(1);
        }

        // Act
        Exception? error;
        if (stage == "authorization")
        {
            error = await Record.ExceptionAsync(() => fixture.Store.PinAuthorizationAsync(fixture.Reference,
                fixture.Authority.SigningCall(), fixture.Token));
        }
        else
        {
            error = await Record.ExceptionAsync(() => fixture.PinAsync());
        }

        // Assert
        Assert.NotNull(error);
        Assert.False(File.Exists(fixture.PathFor(stage)));
        Assert.Equal(logs, fixture.Logs.Messages.Count);
    }

    [Fact]
    public async Task SecondValidDerForSameTemplate_CannotReplacePinnedResponse()
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        await fixture.AuthorizeAsync();
        EnrollmentIssuedArtifact original = await fixture.SignAsync();
        await fixture.PinAsync();
        LocalVoterCredentialIntent intent = fixture.Local!.Intent;
        using X509Certificate2 other = EnrollmentCertificates.IssueLeaf(fixture.Authority.Scenario.Crypto.Issuer,
            VoterCredentialProofs.CertificateIdentity(intent.Request),
            intent.Request.Renewal.Issuance, Convert.FromBase64String(intent.CsrDer));
        EnrollmentIssuedArtifact changed = original with
        {
            CertificateDer = other.RawData, CertificateDigest = EnrollmentCertificates.Hash(other.RawData)
        };
        Assert.NotEqual(original.CertificateDigest, changed.CertificateDigest);
        byte[] expected = File.ReadAllBytes(fixture.PathFor("artifact"));

        // Act
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.PinArtifactAsync(fixture.Reference,
            fixture.Authority.SigningCall(), changed, fixture.Token));
        fixture.Reopen();
        LocalVoterCredentialState read = await fixture.Store.ReadAsync(fixture.Reference, fixture.Token);

        // Assert
        Assert.Equal(expected, File.ReadAllBytes(fixture.PathFor("artifact")));
        Assert.Equal(original.CertificateDer, read.Artifact!.CertificateDer);
        Assert.Equal(3, fixture.Logs.Messages.Count);
    }

    [Fact]
    public async Task ArtifactWithoutAuthorizationPin_CannotBePersisted()
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        await fixture.PrepareAsync();
        await fixture.Authority.AuthorizeAsync();
        await fixture.SignAsync();

        // Act
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PinAsync());

        // Assert
        Assert.False(File.Exists(fixture.PathFor("artifact")));
        Assert.Single(fixture.Logs.Messages);
        Assert.Equal(2, fixture.Authority.Scenario.State.Index);
    }

    [Fact]
    public async Task CrossSlotCopy_IsNotAcceptedAsAnotherIntent()
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        await fixture.PrepareAsync();
        string different = new string('A', 64);
        string directory = Path.Combine(fixture.Authority.Installation.Options.SecretDirectory, "voter-renewal", different);
        ProtectedInstallationFiles.EnsureDirectory(directory);
        ProtectedInstallationFiles.WriteNew(Path.Combine(directory, "intent"),
            File.ReadAllBytes(fixture.PathFor("intent")));

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.Store.ReadAsync(different, fixture.Token));

        // Assert
        Assert.NotNull(error);
        Assert.Empty(fixture.Authority.Journal.Issued);
    }

    [Fact]
    public async Task HistoricalReadAfterExpiry_KeepsPinsWithoutGrantingSigningAuthority()
    {
        // Arrange
        using LocalVoterCredentialFixture fixture = new();
        await fixture.AuthorizeAsync();
        EnrollmentIssuedArtifact original = await fixture.SignAsync();
        LocalVoterCredentialState pinned = await fixture.PinAsync();
        fixture.Authority.Time.Now = pinned.Intent.Request.Renewal.Issuance.NotAfterUtc.AddDays(1);
        fixture.Reopen();

        // Act
        LocalVoterCredentialState read = await fixture.Store.ReadAsync(fixture.Reference, fixture.Token);
        Exception? signing = await Record.ExceptionAsync(() => fixture.SignAsync());

        // Assert
        Assert.Equal(pinned.Intent, read.Intent);
        Assert.Equal(original.CertificateDer, read.Artifact!.CertificateDer);
        Assert.NotNull(signing);
        Assert.Equal(1, fixture.Authority.Journal.Commits);
        Assert.Equal(3, fixture.Logs.Messages.Count);
    }
}
