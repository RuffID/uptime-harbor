using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class EnrollmentPeerTrustTests
{
    [Theory]
    [InlineData(72, "before", true)]
    [InlineData(72, "deadline", false)]
    [InlineData(6, "before", true)]
    [InlineData(6, "deadline", false)]
    [InlineData(72, "retired", false)]
    [InlineData(72, "revoked", false)]
    [InlineData(72, "missing", false)]
    [InlineData(72, "extended", false)]
    public async Task DedicatedRegistryAndPresentedLeafRespectTheSameOverlap(int hoursToExpiry, string stage, bool expected)
    {
        // Arrange
        using EnrollmentTrustFixture fixture = new();
        RevisionImage image = fixture.Accepted();
        OperationalCredential old = image.Operations.Credentials.Values.Single();
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(old.PublicCertificate));
        DateTimeOffset bound = old.ExpiresAtUtc.AddHours(-hoursToExpiry);
        DateTimeOffset deadline = bound.AddHours(Math.Min(hoursToExpiry, 24));
        DateTimeOffset now = stage == "deadline" ? deadline : deadline.AddSeconds(-1);
        VoterCredentialRotation rotation = Rotation(fixture.Crypto, old, bound, deadline);
        rotation = stage switch
        {
            "retired" => rotation with { RetiredAtUtc = now, RetiredIndex = 5 },
            "missing" => rotation with { OverlapUntilUtc = null },
            "extended" => rotation with { OverlapUntilUtc = deadline.AddHours(1) },
            _ => rotation
        };
        if (stage == "revoked")
        {
            old = old with { RevokedAtUtc = now };
        }

        image = WithRotation(image, old, rotation);
        RevisionStateStore state = new();
        state.Publish(image);
        AcceptedPeerTrustSource trust = new(state, new FixedTime(now));

        // Act
        PeerCredential? peer = await trust.FindAsync(old.Id, TestContext.Current.CancellationToken);
        bool valid = EnrollmentLiveTrust.Validate(image, leaf, now);

        // Assert
        Assert.Equal(expected, peer is not null);
        Assert.Equal(expected, valid);
    }

    [Fact]
    public async Task LegacyProxyStillRequiresItsChainAndTheExactUsableEnvelopeCredential()
    {
        // Arrange
        using EnrollmentTrustFixture fixture = new();
        RevisionImage image = fixture.Accepted();
        OperationalCredential old = image.Operations.Credentials.Values.Single();
        DateTimeOffset bound = fixture.Crypto.Now.AddDays(25);
        DateTimeOffset deadline = bound.AddDays(1);
        DateTimeOffset now = deadline.AddSeconds(-1);
        using ECDsa caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest rootRequest = new("CN=Manual cluster CA", caKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 root = rootRequest.CreateSelfSigned(bound.AddDays(-1), old.ExpiresAtUtc.AddDays(1));
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=Manual cluster voter", key, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder names = new();
        names.AddUri(new Uri($"urn:uptimeharbor:{image.ClusterId:D}:{image.Generation}:{old.NodeId:D}"));
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using X509Certificate2 leaf = request.Create(root, bound.AddHours(-1), old.ExpiresAtUtc, [1]);
        old = old with { Fingerprint = EnrollmentCertificates.Hash(leaf.RawData), PublicCertificate = Convert.ToBase64String(leaf.RawData) };
        image = WithRotation(image, old, Rotation(fixture.Crypto, old, bound, deadline));
        RevisionStateStore state = new();
        state.Publish(image);
        FixedTime clock = new(now);
        AcceptedPeerTrustSource trust = new(state, clock);
        ClusterOptions options = new() { ClusterId = image.ClusterId, Generation = image.Generation,
            NodeId = Guid.NewGuid(), ClientCaFingerprint = EnrollmentCertificates.Hash(root.RawData), TlsBoundary = "NginxIsolatedHttp" };
        PeerEnvelope envelope = new(image.ClusterId, image.Generation, old.NodeId, options.NodeId,
            fixture.Crypto.Request.StorageGeneration, old.Id, Guid.NewGuid(), now, options.Compatibility, Guid.NewGuid());
        PeerProtocolGuard guard = new(options, trust, clock);
        CancellationToken token = TestContext.Current.CancellationToken;

        // Act
        PeerCredential? before = await guard.ValidateAsync(envelope, 0, 100, token);
        clock.Now = deadline;
        PeerCredential? at = await guard.ValidateAsync(envelope, 0, 100, token);

        // Assert
        Assert.NotNull(before);
        PeerCertificateValidator validator = new();
        Assert.True(validator.Validate(leaf, root, options, before, now));
        Assert.False(validator.Validate(leaf, fixture.Crypto.Issuer, options, before, now));
        Assert.False(validator.Validate(leaf, root, options, before with { Fingerprint = new string('0', 64) }, now));
        options.TlsBoundary = "Direct";
        Assert.False(validator.Validate(leaf, root, options, before, now, offlineRevocation: true));
        Assert.False(EnrollmentLiveTrust.Validate(image, leaf, now));
        Assert.Null(at);
    }

    [Fact]
    public async Task AcceptedIssuerRevocationClosesExistingRpcTrustWhileRetirementRetainsValidLeaf()
    {
        // Arrange
        using EnrollmentTrustFixture fixture = new();
        RevisionStateStore state = new();
        RevisionImage image = fixture.Accepted();
        state.Publish(image);
        AcceptedPeerTrustSource source = new(state, new FixedTime(fixture.Crypto.Now));
        AcceptedEnrollmentIssuer issuer = image.OnboardingContracts!.Issuers![fixture.Crypto.Epoch];

        // Act
        IReadOnlyList<PeerCredential> active = await source.ListAsync(TestContext.Current.CancellationToken);
        state.Publish(image with { OnboardingContracts = image.OnboardingContracts with
        {
            Issuers = image.OnboardingContracts.Issuers.SetItem(issuer.Epoch, issuer with { RetiredAtUtc = fixture.Crypto.Now })
        } });
        IReadOnlyList<PeerCredential> retired = await source.ListAsync(TestContext.Current.CancellationToken);
        state.Publish(image with { OnboardingContracts = image.OnboardingContracts with
        {
            Issuers = image.OnboardingContracts.Issuers.SetItem(issuer.Epoch, issuer with { RevokedAtUtc = fixture.Crypto.Now })
        } });
        IReadOnlyList<PeerCredential> revoked = await source.ListAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(active);
        Assert.Single(retired);
        Assert.Empty(revoked);
    }

    private static VoterCredentialRotation Rotation(EnrollmentCertificateFixture crypto, OperationalCredential old,
        DateTimeOffset bound, DateTimeOffset deadline)
    {
        EnrollmentIssuance successor = crypto.Template with { IssueId = Guid.NewGuid() };
        return new(new(old.Id, old.Fingerprint, crypto.Template.SpkiDigest, 0, successor), Guid.NewGuid(), 3,
            new string('0', 64), bound) { CredentialRevision = 4, BoundAtUtc = bound, OverlapUntilUtc = deadline };
    }

    private static RevisionImage WithRotation(RevisionImage image, OperationalCredential old, VoterCredentialRotation rotation)
    {
        return image with
        {
            Operations = image.Operations with { Credentials = image.Operations.Credentials.SetItem(old.Id, old) },
            OnboardingContracts = image.OnboardingContracts! with { VoterCredentialRotations =
                (image.OnboardingContracts!.VoterCredentialRotations ?? []).SetItem(rotation.Renewal.Issuance.IssueId, rotation) }
        };
    }

    private class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }
}
