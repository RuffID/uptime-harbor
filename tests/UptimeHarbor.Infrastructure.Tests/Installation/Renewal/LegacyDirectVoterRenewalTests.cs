using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class LegacyDirectVoterRenewalTests
{
    [Fact]
    public async Task ManualVoter_RetriesSigningAndBind_ThenDedicatedLeafUsesAcceptedTrust()
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new(manual: true);
        VoterCredentialAuthorityFixture authority = fixture.Local.Authority;
        await fixture.Local.AuthorizeAsync();
        authority.Remote.LoseResponse = true;

        // Act
        await Assert.ThrowsAsync<IOException>(() => fixture.Local.SignAsync());
        authority.Remote.LoseResponse = false;
        EnrollmentIssuedArtifact artifact = await fixture.Local.SignAsync();
        await fixture.Local.PinAsync();

        // Assert
        Assert.Equal(1, authority.Journal.Commits);
        Assert.Equal(2, authority.Remote.Calls);
        Assert.Equal(authority.Journal.Issued[artifact.IssueId].CertificateDer, artifact.CertificateDer);
        using X509Certificate2 next = X509CertificateLoader.LoadCertificate(artifact.CertificateDer);
        using X509Chain presented = new();
        Assert.False(LegacyDirectPeerTrust.ValidateClient(next, authority.SubjectOptions, authority.Scenario.State, authority.Time.Now));
        Assert.Null(LegacyPeerTransport.SelectServerTrust(next, presented, authority.Time.Now, () => authority.Scenario.State));

        // Act
        authority.ManualRevocationStatus = X509ChainStatusFlags.OfflineRevocation;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.BindAsync());
        authority.ManualRevocationStatus = X509ChainStatusFlags.NoError;
        fixture.Transport.LoseResponse = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.BindAsync());

        // Assert
        Assert.Single(fixture.Transport.Sent);
        RevisionImage accepted = authority.Scenario.State;
        OperationalCredential fresh = accepted.Operations.Credentials[artifact.IssueId];
        OperationalCredential old = accepted.Operations.Credentials[authority.Request.Renewal.PreviousCredentialId];
        using X509Certificate2 prior = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(old.PublicCertificate));
        Assert.True(VoterCredentialLeafTrust.ValidatePrevious(accepted, old, prior, authority.Time.Now, authority.SubjectAuthority.LegacyTrust));
        Assert.True(LegacyDirectPeerTrust.ValidateClient(next, authority.SubjectOptions, accepted, authority.Time.Now, Peer(accepted, fresh)));
        Assert.False(LegacyDirectPeerTrust.ValidateClient(next, authority.SubjectOptions, accepted, authority.Time.Now, Peer(accepted, old)));
        PeerServerPin.ChainProof proof = Assert.IsType<PeerServerPin.ChainProof>(
            LegacyPeerTransport.SelectServerTrust(next, presented, authority.Time.Now, () => authority.Scenario.State));
        Assert.True(proof.Validate(authority.Time.Now));

        // Act
        DateTimeOffset overlapEnd = authority.Time.Now.AddHours(24);
        authority.Time.Now = overlapEnd;
        authority.ManualRevocationStatus = X509ChainStatusFlags.OfflineRevocation;
        fixture.Reopen();
        LocalVoterCredentialBinding replay = await fixture.BindAsync();

        // Assert
        Assert.Equal(fixture.Intent.Command.OperationId, replay.Result.OperationId);
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(2, authority.Remote.Calls);
        Assert.False(VoterCredentialLeafTrust.ValidatePrevious(accepted, old, prior, overlapEnd, authority.SubjectAuthority.LegacyTrust));
        Assert.True(proof.Validate(overlapEnd));

        // Act
        authority.Scenario.State = accepted with
        {
            Operations = accepted.Operations with
            {
                Credentials = accepted.Operations.Credentials.SetItem(fresh.Id, fresh with { RevokedAtUtc = overlapEnd })
            }
        };

        // Assert
        Assert.False(proof.Validate(overlapEnd));
        Assert.False(LegacyDirectPeerTrust.ValidateClient(next, authority.SubjectOptions, authority.Scenario.State, overlapEnd));
    }

    [Theory]
    [InlineData(X509ChainStatusFlags.OfflineRevocation)]
    [InlineData(X509ChainStatusFlags.Revoked)]
    public async Task ManualCrlFailureRejectsAuthorizeAndSigningBeforeAnyEffect(X509ChainStatusFlags status)
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new(remote: true, manual: true);
        fixture.ManualRevocationStatus = status;

        // Act
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.PrepareAsync());
        fixture.ManualRevocationStatus = X509ChainStatusFlags.NoError;
        await fixture.AuthorizeAsync();
        fixture.ManualRevocationStatus = status;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Router().IssueAsync(fixture.SigningCall(), TestContext.Current.CancellationToken));

        // Assert
        Assert.Empty(fixture.Journal.Issued);
        Assert.Equal(0, fixture.Remote.Calls);
    }

    [Fact]
    public async Task ConfiguredManualRootWithoutCrlCannotPassProductionProofPolicy()
    {
        // Arrange
        using VoterCredentialAuthorityFixture fixture = new(remote: true, manual: true);
        await fixture.PrepareAsync();

        // Act
        UnauthorizedAccessException denied = Assert.Throws<UnauthorizedAccessException>(() =>
            VoterCredentialProofs.Require(fixture.Scenario.State, fixture.Call, fixture.Time.Now, new(fixture.SubjectOptions)));

        // Assert
        Assert.Equal("VoterCredentialPreviousLeafInvalid", denied.Message);
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("purpose")]
    [InlineData("issuer-revoked")]
    [InlineData("expired")]
    [InlineData("bind-result")]
    public async Task BoundLeafRejectsChangedIssuerOrAuthorityWithoutManualFallback(string mutation)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new(manual: true);
        await fixture.PrepareAsync();
        LocalVoterCredentialBinding bound = await fixture.BindAsync();
        VoterCredentialAuthorityFixture authority = fixture.Local.Authority;
        RevisionImage accepted = authority.Scenario.State;
        Guid epoch = authority.Scenario.Crypto.Epoch;
        AcceptedEnrollmentIssuer issuer = accepted.OnboardingContracts!.Issuers![epoch];
        EnrollmentIssuedArtifact artifact = fixture.Local.Artifact!;
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(artifact.CertificateDer);
        using X509Chain presented = new();
        using ECDsa substituteKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 substitute = EnrollmentCertificates.CreateIssuer(substituteKey, accepted.ClusterId, accepted.Generation,
            epoch, issuer.NotBeforeUtc);

        DateTimeOffset now = authority.Time.Now;
        switch (mutation)
        {
            case "replacement":
                issuer = issuer with
                {
                    CertificateDer = Convert.ToBase64String(substitute.RawData),
                    CertificateDigest = EnrollmentCertificates.Hash(substitute.RawData),
                    SpkiDigest = EnrollmentCertificates.Hash(substituteKey.ExportSubjectPublicKeyInfo())
                };
                break;
            case "purpose":
                issuer = issuer with { Purpose = "other" };
                break;
            case "issuer-revoked":
                issuer = issuer with { RevokedAtUtc = now };
                break;
            case "expired":
                now = new(leaf.NotAfter.ToUniversalTime());
                break;
            case "bind-result":
                accepted = accepted with { Results = accepted.Results.Remove(bound.Result.OperationId) };
                break;
        }

        accepted = accepted with
        {
            OnboardingContracts = accepted.OnboardingContracts with { Issuers = accepted.OnboardingContracts.Issuers.SetItem(epoch, issuer) }
        };
        // Несуществующий legacy CA выявляет попытку переключиться на ручной путь после отказа dedicated.
        authority.SubjectOptions.ClientCaPath = Path.Combine(authority.Installation.Root, "missing-ca.der");

        // Act
        bool client = LegacyDirectPeerTrust.ValidateClient(leaf, authority.SubjectOptions, accepted, now);
        PeerServerPin.ChainProof? server = LegacyPeerTransport.SelectServerTrust(leaf, presented, now, () => accepted);

        // Assert
        Assert.False(client);
        Assert.Null(server);
    }

    private static PeerCredential Peer(RevisionImage image, OperationalCredential credential)
    {
        OperationalNode node = image.Operations.Nodes[credential.NodeId];

        return new(credential.Id, node.NodeId, node.StorageGeneration, new(node.Address),
            credential.Fingerprint, node.Voting, false, credential.ExpiresAtUtc);
    }
}
