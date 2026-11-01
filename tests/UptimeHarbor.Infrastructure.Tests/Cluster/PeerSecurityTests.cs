using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class PeerSecurityTests
{
    [Theory]
    [InlineData("cluster")]
    [InlineData("node")]
    [InlineData("generation")]
    [InlineData("storage")]
    [InlineData("version")]
    [InlineData("time")]
    [InlineData("size")]
    [InlineData("credential")]
    public async Task GuardRejectsInvalidEnvelopeBeforePayload(string failure)
    {
        // Arrange
        ClusterOptions options = new() { ClusterId = Guid.NewGuid(), Generation = 1, NodeId = Guid.NewGuid() };
        TrustStub trust = new();
        PeerEnvelope envelope = new(options.ClusterId, 1, trust.Credential.NodeId, options.NodeId,
            trust.Credential.StorageGeneration, trust.Credential.CredentialId, Guid.NewGuid(),
            DateTimeOffset.UtcNow, options.Compatibility, Guid.NewGuid());
        long size = 10;
        switch (failure)
        {
            case "cluster":
                envelope = envelope with { ClusterId = Guid.NewGuid() };
                break;
            case "node":
                envelope = envelope with { FromNodeId = Guid.NewGuid() };
                break;
            case "generation":
                envelope = envelope with { Generation = 2 };
                break;
            case "storage":
                envelope = envelope with { StorageGeneration = Guid.NewGuid() };
                break;
            case "version":
                envelope = envelope with { Compatibility = "2.0" };
                break;
            case "time":
                envelope = envelope with { IssuedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-31) };
                break;
            case "size":
                size = 65537;
                break;
            case "credential":
                envelope = envelope with { CredentialId = Guid.NewGuid() };
                break;
        }

        PeerProtocolGuard guard = new(options, trust, TimeProvider.System);

        // Act
        PeerCredential? valid = await guard.ValidateAsync(envelope, size, 65536, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(valid);
    }

    [Fact]
    public async Task GuardAcceptsMatchingIdentityAndFingerprintIsCheckedSeparately()
    {
        // Arrange
        ClusterOptions options = new() { ClusterId = Guid.NewGuid(), Generation = 1, NodeId = Guid.NewGuid() };
        TrustStub trust = new();
        PeerEnvelope envelope = new(options.ClusterId, 1, trust.Credential.NodeId, options.NodeId,
            trust.Credential.StorageGeneration, trust.Credential.CredentialId, Guid.NewGuid(),
            DateTimeOffset.UtcNow, options.Compatibility, Guid.NewGuid());
        PeerProtocolGuard guard = new(options, trust, TimeProvider.System);

        // Act
        PeerCredential? valid = await guard.ValidateAsync(envelope, 100, 65536, TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(trust.Credential, valid);
    }

    [Fact]
    public void ReplayInboxRejectsDuplicateAfterReopenButExpiresItAfterWindow()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        string path = Path.Combine(directory.Location, "inbox");
        Guid node = Guid.NewGuid();
        Guid request = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using (PeerReplayInbox first = new(path, 100, now))
        {
            Assert.True(first.TryAccept(node, request, now));
        }

        // Act
        using PeerReplayInbox reopened = new(path, 100, now);
        bool replay = reopened.TryAccept(node, request, now.AddSeconds(30));
        bool expired = reopened.TryAccept(node, request, now.AddSeconds(121));

        // Assert
        Assert.False(replay);
        Assert.True(expired);
    }

    [Fact]
    public void ClientCertificateRequiresPinnedFingerprintAndClusterSan()
    {
        // Arrange
        using RSA caKey = RSA.Create(2048);
        CertificateRequest rootRequest = new("CN=Cluster test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        ClusterOptions options = new() { ClusterId = Guid.NewGuid(), Generation = 1,
            TlsBoundary = "NginxIsolatedHttp", ClientCaFingerprint = root.GetCertHashString(HashAlgorithmName.SHA256) };
        TrustStub trust = new();
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=Node", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new();
        names.AddUri(new Uri($"urn:uptimeharbor:{options.ClusterId:D}:1:{trust.Credential.NodeId:D}"));
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using X509Certificate2 certificate = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        PeerCredential credential = trust.Credential with { Fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256) };
        PeerCertificateValidator validator = new();
        Assert.True(PeerCertificateValidator.HasIdentity(certificate.Extensions["2.5.29.17"]!.RawData,
            $"urn:uptimeharbor:{options.ClusterId:D}:1:{credential.NodeId:D}"));
        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(chain.Build(certificate), string.Join(",", chain.ChainStatus.Select(status => status.Status)));

        // Act
        bool valid = validator.Validate(certificate, root, options, credential, DateTimeOffset.UtcNow);
        bool wrongFingerprint = validator.Validate(certificate, root, options, trust.Credential, DateTimeOffset.UtcNow);
        options.Generation = 2;
        bool wrongSan = validator.Validate(certificate, root, options, credential, DateTimeOffset.UtcNow);

        // Assert
        Assert.True(valid);
        Assert.False(wrongFingerprint);
        Assert.False(wrongSan);
    }

    private class TrustStub : IPeerTrustSource
    {
        public PeerCredential Credential { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new Uri("https://a.example/internal/raft"), new string('A', 64), true, false, DateTimeOffset.UtcNow.AddHours(1));

        public ValueTask<PeerCredential?> FindAsync(Guid id, CancellationToken cancellationToken)
        {
            if (id == Credential.CredentialId)
            {
                return ValueTask.FromResult<PeerCredential?>(Credential);
            }

            return ValueTask.FromResult<PeerCredential?>(null);
        }

        public ValueTask<IReadOnlyList<PeerCredential>> ListAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<PeerCredential>>([Credential]);
        }
    }
}
