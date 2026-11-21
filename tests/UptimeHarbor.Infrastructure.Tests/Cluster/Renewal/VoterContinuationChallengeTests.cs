using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

public class VoterContinuationChallengeTests
{
    [Fact]
    public async Task ExactRequestStatusAndRegistrationRequireDistinctFreshProofs()
    {
        // Arrange
        using VoterContinuationScenario scenario = new();
        VoterCredentialProofRequest start = Challenge(scenario, VoterContinuationAuthority.REQUEST);

        // Act
        string nonce = await Issue(scenario, start, VoterContinuationAuthority.REQUEST);

        // Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Issue(scenario, start, VoterContinuationAuthority.REQUEST));
        VoterCredentialProofRequest request = Proof(scenario, nonce);

        // Act
        CommandResult accepted = await scenario.Authority.RequestAsync(request, scenario.Token);

        // Assert
        Assert.Equal(OperationOutcome.Committed, accepted.Outcome);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scenario.Authority.RequestAsync(request, scenario.Token));

        // Act
        VoterContinuationStatus pending = await Status(scenario);

        // Assert
        Assert.Equal(accepted, pending.Request);
        Assert.Null(pending.AuthorizationDigest);
        Assert.Null(pending.Registration);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Issue(scenario,
            Challenge(scenario, VoterContinuationAuthority.REGISTER, accepted.Digest), VoterContinuationAuthority.REGISTER, accepted.Digest));

        CommandResult approval = await scenario.ApproveAsync(accepted);
        Assert.Equal(approval.Digest, (await Status(scenario)).AuthorizationDigest);
        string registerNonce = await Issue(scenario,
            Challenge(scenario, VoterContinuationAuthority.REGISTER, approval.Digest), VoterContinuationAuthority.REGISTER, approval.Digest);
        VoterCredentialProofRequest registration = Proof(scenario, registerNonce);

        // Act
        CommandResult result = await scenario.Authority.RegisterAsync(registration, approval.Digest, scenario.Token);

        // Assert
        Assert.Equal(OperationOutcome.Committed, result.Outcome);
        Assert.Equal(result, (await Status(scenario)).Registration);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scenario.Authority.RegisterAsync(registration, approval.Digest, scenario.Token));
        Assert.Equal(3, scenario.Wal.Count);
    }

    [Theory]
    [InlineData("purpose")]
    [InlineData("audience")]
    [InlineData("key")]
    [InlineData("csr")]
    [InlineData("storage")]
    [InlineData("issuer")]
    [InlineData("removed")]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("follower")]
    public async Task ChallengeRefusesAlteredOrStaleEvidence(string fault)
    {
        // Arrange
        using VoterContinuationScenario scenario = new();
        string purpose = VoterContinuationAuthority.REQUEST;
        string? audience = null;
        if (fault == "purpose")
        {
            purpose = VoterContinuationAuthority.STATUS;
        }

        if (fault == "audience")
        {
            audience = "https://different.invalid/internal/raft";
        }

        VoterCredentialProofRequest call = Challenge(scenario, purpose, audience: audience);
        RevisionImage image = scenario.State;
        var operations = image.Operations;
        var contracts = image.OnboardingContracts!;
        Guid oldId = call.Request.Renewal.PreviousCredentialId;
        Guid voter = call.Proof.OriginNodeId;
        Guid epoch = call.Request.Renewal.Issuance.IssuerEpoch;
        switch (fault)
        {
            case "key":
                byte[] badSignature = scenario.Crypto.NewKey.SignData([1, 2, 3], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
                call = call with { Proof = call.Proof with { Signature = Convert.ToBase64String(badSignature) } };
                break;
            case "csr":
                call = call with { CsrDer = "AAAA" };
                break;
            case "storage":
                operations = operations with { Nodes = operations.Nodes.SetItem(voter, operations.Nodes[voter] with { StorageGeneration = Guid.NewGuid() }) };
                break;
            case "issuer":
                contracts = contracts with { Issuers = contracts.Issuers!.SetItem(epoch, contracts.Issuers[epoch] with { RevokedAtUtc = scenario.Time.Now }) };
                break;
            case "removed":
                operations = operations with { Nodes = operations.Nodes.SetItem(voter, operations.Nodes[voter] with { Removed = true }) };
                break;
            case "revoked":
                operations = operations with { Credentials = operations.Credentials.SetItem(oldId, operations.Credentials[oldId] with { RevokedAtUtc = scenario.Time.Now }) };
                break;
            case "expired":
                scenario.Time.Now = scenario.Time.Now.AddSeconds(60);
                break;
            case "follower":
                scenario.NativeLeader = false;
                break;
        }

        scenario.Store.Publish(image with { Operations = operations, OnboardingContracts = contracts });

        // Act
        Exception? error = await Record.ExceptionAsync(() => Issue(scenario, call, VoterContinuationAuthority.REQUEST));

        // Assert
        if (fault == "csr")
        {
            Assert.IsType<InvalidDataException>(error);
        }
        else
        {
            Assert.IsType<UnauthorizedAccessException>(error);
        }

        Assert.Empty(scenario.Wal);
    }

    [Theory]
    [InlineData("nonce-expiry")]
    [InlineData("revision")]
    [InlineData("majority")]
    public async Task FreshProofCannotReuseChallengeAfterExpiryOrAuthorityChange(string fault)
    {
        // Arrange
        using VoterContinuationScenario scenario = new();
        string nonce = await Issue(scenario, Challenge(scenario, VoterContinuationAuthority.REQUEST), VoterContinuationAuthority.REQUEST);
        switch (fault)
        {
            case "majority":
                scenario.Barrier.Available = false;
                break;
            case "nonce-expiry":
                scenario.Time.Stamp += TimeSpan.FromSeconds(60).Ticks;
                break;
            case "revision":
                var contracts = scenario.State.OnboardingContracts!;
                var issuers = contracts.Issuers!;
                var issuer = issuers[scenario.Request.Renewal.Issuance.IssuerEpoch];
                scenario.Store.Publish(scenario.State with
                {
                    OnboardingContracts = contracts with { Issuers = issuers.SetItem(issuer.Epoch, issuer with { Revision = issuer.Revision + 1 }) }
                });
                break;
        }

        // Act
        Exception? error = await Record.ExceptionAsync(() => scenario.Authority.RequestAsync(Proof(scenario, nonce), scenario.Token));

        // Assert
        if (fault == "majority")
        {
            Assert.IsType<InvalidOperationException>(error);
        }
        else
        {
            Assert.IsType<UnauthorizedAccessException>(error);
        }

        Assert.Empty(scenario.Wal);
    }

    [Fact]
    public async Task StatusCannotUseRequestNonceOrOpenDifferentIntentWithSameId()
    {
        // Arrange
        using VoterContinuationScenario scenario = new();
        CommandResult accepted = await scenario.ProposeAsync();
        VoterCredentialProofRequest requestProof = await scenario.ProveAsync();
        VoterCredentialRequest altered = scenario.Request with
        {
            Renewal = scenario.Request.Renewal with { Issuance = scenario.Request.Renewal.Issuance with { IssueId = Guid.NewGuid() } }
        };

        // Act
        Exception? wrongNonce = await Record.ExceptionAsync(() => scenario.Authority.ReadStatusAsync(requestProof, scenario.Token));
        Exception? wrongIntent = await Record.ExceptionAsync(() => Issue(scenario,
            Challenge(scenario, VoterContinuationAuthority.STATUS, request: altered), VoterContinuationAuthority.STATUS));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(wrongNonce);
        Assert.IsType<UnauthorizedAccessException>(wrongIntent);

        string nonce = await Issue(scenario, Challenge(scenario, VoterContinuationAuthority.STATUS), VoterContinuationAuthority.STATUS);
        VoterCredentialProofRequest proof = Proof(scenario, nonce);

        // Act
        VoterContinuationStatus status = await scenario.Authority.ReadStatusAsync(proof, scenario.Token);

        // Assert
        Assert.Equal(accepted, status.Request);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scenario.Authority.ReadStatusAsync(proof, scenario.Token));
        Assert.Single(scenario.Wal);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("term")]
    [InlineData("approval-expiry")]
    [InlineData("lease-expiry")]
    public async Task PreviouslyAcceptedStatusClosesWhenAuthorityChanges(string fault)
    {
        // Arrange
        using VoterContinuationScenario scenario = new();
        CommandResult approved = await scenario.ApproveAsync(await scenario.ProposeAsync());
        int expectedWal = 2;
        if (fault != "approval-expiry")
        {
            await scenario.RegisterAsync(approved);
            expectedWal++;
        }

        string nonce = await Issue(scenario, Challenge(scenario, VoterContinuationAuthority.STATUS), VoterContinuationAuthority.STATUS);
        switch (fault)
        {
            case "actor":
                scenario.ChangeActor(false);
                break;
            case "term":
                scenario.Term++;
                break;
            case "approval-expiry":
                scenario.Time.Now = scenario.Time.Now.AddMinutes(5);
                break;
            case "lease-expiry":
                scenario.Time.Now = scenario.Time.Now.AddMinutes(2);
                break;
        }

        // Act
        Exception? error = await Record.ExceptionAsync(() => scenario.Authority.ReadStatusAsync(Proof(scenario, nonce), scenario.Token));

        // Assert
        UnauthorizedAccessException refusal = Assert.IsType<UnauthorizedAccessException>(error);
        if (fault == "approval-expiry")
        {
            Assert.Equal("VoterContinuationAuthorizationExpiredOrChanged", refusal.Message);
        }

        Assert.Equal(expectedWal, scenario.Wal.Count);
    }

    private static VoterCredentialProofRequest Challenge(VoterContinuationScenario scenario, string purpose, string? approval = null,
        string? audience = null, VoterCredentialRequest? request = null)
    {
        request ??= scenario.Request;
        VoterCredentialProof candidate = scenario.Candidate with
        {
            IssuedAtUtc = scenario.Time.Now, Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        };
        return new(request, VoterCredentialContinuationProofs.SignChallenge(request, candidate, scenario.Crypto.Crypto.PeerKey,
            purpose, audience ?? scenario.Options.PublicUri, approval), Convert.ToBase64String(scenario.Crypto.Csr));
    }

    private static VoterCredentialProofRequest Proof(VoterContinuationScenario scenario, string nonce)
    {
        VoterCredentialProof proof = VoterCredentialContinuationProofs.Sign(scenario.Request,
            scenario.Candidate with { IssuedAtUtc = scenario.Time.Now, Nonce = nonce }, scenario.Crypto.Crypto.PeerKey);
        return new(scenario.Request, proof, Convert.ToBase64String(scenario.Crypto.Csr));
    }

    private static Task<string> Issue(VoterContinuationScenario scenario, VoterCredentialProofRequest call, string purpose, string? approval = null)
    {
        return scenario.Authority.IssueChallengeAsync(call, purpose, approval, scenario.Token);
    }

    private static async Task<VoterContinuationStatus> Status(VoterContinuationScenario scenario)
    {
        string nonce = await Issue(scenario, Challenge(scenario, VoterContinuationAuthority.STATUS), VoterContinuationAuthority.STATUS);
        return await scenario.Authority.ReadStatusAsync(Proof(scenario, nonce), scenario.Token);
    }
}
