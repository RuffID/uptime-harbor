using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class PeerServerPinTests
{
    [Fact]
    public async Task AcceptedCertificateMustBelongToExactServerAuthorityIncludingPort()
    {
        // Arrange
        using EnrollmentCertificateFixture crypto = new();
        using EnrollmentCertificateFixture foreign = new();
        using X509Certificate2 leaf = crypto.Issue();
        using X509Certificate2 other = foreign.Issue();
        Trust trust = new(Credential(crypto, leaf));

        // Act
        bool valid = await PeerServerPin.MatchesAsync(new("https://peer.example.test/internal/cluster/v1/health"), leaf,
            trust, crypto.Now, TestContext.Current.CancellationToken);
        bool wrongPeer = await PeerServerPin.MatchesAsync(new("https://peer.example.test/internal/cluster/v1/health"), other,
            trust, crypto.Now, TestContext.Current.CancellationToken);
        bool wrongPort = await PeerServerPin.MatchesAsync(new("https://peer.example.test:444/internal/cluster/v1/health"), leaf,
            trust, crypto.Now, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(valid);
        Assert.False(wrongPeer);
        Assert.False(wrongPort);
    }

    [Fact]
    public async Task RevocationBetweenRegistryReadsAndExpiryRejectAnEstablishedTlsCertificate()
    {
        // Arrange
        using EnrollmentCertificateFixture crypto = new();
        using X509Certificate2 leaf = crypto.Issue();
        Trust trust = new(Credential(crypto, leaf)) { RevokedDuringLookup = true };

        // Act
        bool revoked = await PeerServerPin.MatchesAsync(new("https://peer.example.test/internal/cluster/v1/health"), leaf,
            trust, crypto.Now, TestContext.Current.CancellationToken);
        trust.RevokedDuringLookup = false;
        bool expired = await PeerServerPin.MatchesAsync(new("https://peer.example.test/internal/cluster/v1/health"), leaf,
            trust, crypto.Template.NotAfterUtc, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(revoked);
        Assert.False(expired);
    }

    private static PeerCredential Credential(EnrollmentCertificateFixture crypto, X509Certificate2 leaf)
    {
        return new(crypto.Template.IssueId, crypto.Request.NodeId, crypto.Request.StorageGeneration, new(crypto.Request.PeerUri),
            EnrollmentCertificates.Hash(leaf.RawData), true, false, crypto.Template.NotAfterUtc);
    }

    private class Trust(PeerCredential credential) : IPeerTrustSource
    {
        public bool RevokedDuringLookup { get; set; }

        public ValueTask<PeerCredential?> FindAsync(Guid id, CancellationToken token)
        {
            if (RevokedDuringLookup)
            {
                return ValueTask.FromResult<PeerCredential?>(null);
            }

            return ValueTask.FromResult<PeerCredential?>(credential);
        }

        public ValueTask<IReadOnlyList<PeerCredential>> ListAsync(CancellationToken token)
        {
            return ValueTask.FromResult<IReadOnlyList<PeerCredential>>([credential]);
        }
    }
}
