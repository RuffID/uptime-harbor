using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class EnrollmentProofTests
{
    [Fact]
    public void ProofCoversRequestAndProcessWhileNewNoncePreservesImmutableDigest()
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();
        EnrollmentProof proof = Proof(fixture.Request);

        // Act
        EnrollmentProof signed = EnrollmentProofs.Sign(fixture.Request, proof, fixture.PeerKey);
        EnrollmentProof retry = EnrollmentProofs.Sign(fixture.Request,
            proof with { Nonce = Challenge(), RequestId = Guid.NewGuid() }, fixture.PeerKey);

        // Assert
        Assert.True(EnrollmentProofs.Verify(fixture.Request, signed, fixture.Csr));
        Assert.True(EnrollmentProofs.Verify(fixture.Request, retry, fixture.Csr));
        Assert.Equal(signed.RequestDigest, retry.RequestDigest);
        Assert.NotEqual(signed.Signature, retry.Signature);
        Assert.False(EnrollmentProofs.Verify(fixture.Request, signed with { Signature = retry.Signature }, fixture.Csr));
    }

    [Theory]
    [InlineData("boot")]
    [InlineData("challenge")]
    [InlineData("nonce")]
    [InlineData("epoch")]
    [InlineData("action")]
    [InlineData("request")]
    [InlineData("digest")]
    [InlineData("offline")]
    public void ReplayedOrBroadenedProofCannotAuthorizeAnotherProcessOrPurpose(string mutation)
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();
        EnrollmentProof proof = EnrollmentProofs.Sign(fixture.Request, Proof(fixture.Request), fixture.PeerKey);
        EnrollmentProof changed = mutation switch
        {
            "boot" => proof with { BootId = Guid.NewGuid() },
            "challenge" => proof with { BootChallenge = Challenge() },
            "nonce" => proof with { Nonce = Challenge() },
            "epoch" => proof with { ProcessEpoch = 2 },
            "action" => proof with { Action = "EnableOnline" },
            "request" => proof with { RequestId = Guid.NewGuid() },
            "digest" => proof with { RequestDigest = new string('F', 64) },
            "offline" => proof with { Action = "EmergencyRecovery" },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        // Act
        bool accepted = EnrollmentProofs.Verify(fixture.Request, changed, fixture.Csr);

        // Assert
        Assert.False(accepted);
    }

    [Fact]
    public void ForeignKeyCsrAndOversizedSignatureAreRejected()
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();
        using EnrollmentCertificateFixture other = new();
        EnrollmentProof signed = EnrollmentProofs.Sign(fixture.Request, Proof(fixture.Request), fixture.PeerKey);

        // Act
        bool foreign = EnrollmentProofs.Verify(fixture.Request, signed, other.Csr);
        bool large = EnrollmentProofs.Verify(fixture.Request, signed with { Signature = Convert.ToBase64String(new byte[81]) }, fixture.Csr);
        Exception? wrongSigner = Record.Exception(() => EnrollmentProofs.Sign(fixture.Request, Proof(fixture.Request), other.PeerKey));

        // Assert
        Assert.False(foreign);
        Assert.False(large);
        Assert.IsType<InvalidDataException>(wrongSigner);
    }

    [Fact]
    public void NonceIsSingleUseAndHasExactSixtySecondBoundary()
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();
        Clock clock = new();
        EnrollmentNonces nonces = new(clock);
        EnrollmentProof proof = Proof(fixture.Request);
        proof = proof with { Nonce = nonces.Issue(fixture.Request.AttemptId, proof.BootId, proof.BootChallenge, proof.ProcessEpoch, proof.Action, proof.RequestDigest) };

        // Act
        bool once = nonces.Consume(fixture.Request.AttemptId, proof);
        bool replay = nonces.Consume(fixture.Request.AttemptId, proof);
        string expires = nonces.Issue(fixture.Request.AttemptId, proof.BootId, proof.BootChallenge, proof.ProcessEpoch, proof.Action, proof.RequestDigest);
        clock.Elapsed = TimeSpan.FromSeconds(60);
        bool expired = nonces.Consume(fixture.Request.AttemptId, proof with { Nonce = expires });

        // Assert
        Assert.True(once);
        Assert.False(replay);
        Assert.False(expired);
    }

    [Fact]
    public void NonceFromPreviousServerProcessOrOtherBootChallengeIsRejected()
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();
        EnrollmentNonces oldProcess = new(new Clock());
        EnrollmentNonces newProcess = new(new Clock());
        EnrollmentProof proof = Proof(fixture.Request);
        proof = proof with { Nonce = oldProcess.Issue(fixture.Request.AttemptId, proof.BootId, proof.BootChallenge, proof.ProcessEpoch, proof.Action, proof.RequestDigest) };

        // Act
        bool serverRestart = newProcess.Consume(fixture.Request.AttemptId, proof);
        bool clientRestart = oldProcess.Consume(fixture.Request.AttemptId, proof with { BootChallenge = Challenge() });

        // Assert
        Assert.False(serverRestart);
        Assert.False(clientRestart);
    }

    [Fact]
    public void NonceCapacityIsBoundedAndExpiredEntriesReleaseIt()
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();
        Clock clock = new();
        EnrollmentNonces nonces = new(clock);
        EnrollmentProof proof = Proof(fixture.Request);
        foreach (int unused in Enumerable.Range(0, 512))
        {
            nonces.Issue(fixture.Request.AttemptId, proof.BootId, proof.BootChallenge, proof.ProcessEpoch, proof.Action, proof.RequestDigest);
        }

        // Act
        Exception? full = Record.Exception(() => nonces.Issue(fixture.Request.AttemptId, proof.BootId, proof.BootChallenge, proof.ProcessEpoch, proof.Action, proof.RequestDigest));
        clock.Elapsed = TimeSpan.FromSeconds(60);
        string renewed = nonces.Issue(fixture.Request.AttemptId, proof.BootId, proof.BootChallenge, proof.ProcessEpoch, proof.Action, proof.RequestDigest);

        // Assert
        Assert.IsType<InvalidOperationException>(full);
        Assert.True(EnrollmentProofs.IsChallenge(renewed));
    }

    private static EnrollmentProof Proof(EnrollmentRequest request)
    {
        return new(1, Guid.NewGuid(), "ReserveInvitation", Guid.NewGuid(), Challenge(), 1, Challenge(),
            EnrollmentRequestCodec.Digest(request), string.Empty);
    }

    private static string Challenge()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    private class Clock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return Elapsed.Ticks;
        }
    }
}
