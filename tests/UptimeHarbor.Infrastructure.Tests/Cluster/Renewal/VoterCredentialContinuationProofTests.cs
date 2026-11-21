using System.Security.Cryptography;
using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

public class VoterCredentialContinuationProofTests
{
    [Fact]
    public void ExpiredManagedLeafProvesIdentityOnlyInContinuationDomain()
    {
        // Arrange
        using VoterCredentialCryptoScenario scenario = new();
        VoterCredentialProofRequest call = CreateCall(scenario);
        DateTimeOffset expiry = scenario.Crypto.Template.NotAfterUtc;
        VoterCredentialProof sameSignatureInRenewal = call.Proof with { Scope = VoterCredentialProofs.SCOPE };
        VoterCredentialProof renewal = VoterCredentialProofs.Sign(call.Request, sameSignatureInRenewal, scenario.Crypto.PeerKey);

        // Act
        VoterCredentialContinuationProofs.Require(scenario.State, call, expiry);

        // Assert
        Assert.Throws<UnauthorizedAccessException>(() =>
            VoterCredentialProofs.Require(scenario.State, call with { Proof = renewal }, expiry));
        Assert.Throws<InvalidDataException>(() =>
            VoterCredentialContinuationProofs.Require(scenario.State, call with { Proof = sameSignatureInRenewal }, expiry));
        Assert.Throws<UnauthorizedAccessException>(() =>
            VoterCredentialContinuationProofs.Require(scenario.State, call with
            {
                Proof = call.Proof with { Signature = renewal.Signature }
            }, expiry));
        Assert.False(VoterCredentialPolicy.IsUsable(scenario.State,
            scenario.State.Operations.Credentials[call.Request.Renewal.PreviousCredentialId], expiry));
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("nonvoter")]
    [InlineData("storage")]
    [InlineData("address")]
    [InlineData("leaf-revoked")]
    [InlineData("issuer-revoked")]
    [InlineData("membership")]
    [InlineData("maintenance")]
    [InlineData("still-valid")]
    [InlineData("stale")]
    public void RejectsChangedAcceptedIdentityAndInvalidProofTime(string change)
    {
        // Arrange
        using VoterCredentialCryptoScenario scenario = new();
        VoterCredentialProofRequest call = CreateCall(scenario);
        DateTimeOffset now = scenario.Crypto.Template.NotAfterUtc;
        RevisionImage image = ChangeAcceptedImage(scenario, change);
        if (change == "stale")
        {
            now = now.AddSeconds(59);
        }
        else if (change == "still-valid")
        {
            now = now.AddSeconds(-1);
        }

        // Act
        Exception? error = Record.Exception(() => VoterCredentialContinuationProofs.Require(image, call, now));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(error);
    }

    [Fact]
    public void RejectsDifferentIntentNonceCsrAndKey()
    {
        // Arrange
        using VoterCredentialCryptoScenario scenario = new();
        VoterCredentialProofRequest call = CreateCall(scenario);
        DateTimeOffset now = scenario.Crypto.Template.NotAfterUtc;
        VoterCredentialProofRequest otherOperation = call with { Request = call.Request with { OperationId = Guid.NewGuid() } };
        VoterCredentialProofRequest otherNonce = call with
        {
            Proof = call.Proof with { Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }
        };
        VoterCredentialProofRequest otherCsr = call with { CsrDer = Convert.ToBase64String(scenario.Crypto.Csr) };

        // Act
        Exception? operationError = Record.Exception(() => VoterCredentialContinuationProofs.Require(scenario.State, otherOperation, now));
        Exception? nonceError = Record.Exception(() => VoterCredentialContinuationProofs.Require(scenario.State, otherNonce, now));
        Exception? csrError = Record.Exception(() => VoterCredentialContinuationProofs.Require(scenario.State, otherCsr, now));
        Exception? keyError = Record.Exception(() =>
        {
            VoterCredentialContinuationProofs.Sign(call.Request, call.Proof, scenario.NewKey);
        });

        // Assert
        Assert.IsType<UnauthorizedAccessException>(operationError);
        Assert.IsType<UnauthorizedAccessException>(nonceError);
        Assert.IsType<InvalidDataException>(csrError);
        Assert.IsType<InvalidDataException>(keyError);
    }

    [Theory]
    [InlineData("same-active", true)]
    [InlineData("same-expired", false)]
    [InlineData("next-at-expiry", true)]
    [InlineData("next-while-active", false)]
    [InlineData("epoch-skipped", false)]
    [InlineData("same-challenge", false)]
    public void ChecksProcessCandidateAgainstAcceptedLease(string change, bool allowed)
    {
        // Arrange
        using VoterCredentialCryptoScenario scenario = new();
        VoterCredentialProofRequest call = CreateCall(scenario);
        DateTimeOffset expiry = scenario.Crypto.Template.NotAfterUtc;
        VoterCredentialProof proof = call.Proof;
        bool currentBoot = change is "same-active" or "same-expired";
        bool sameChallenge = currentBoot || change == "same-challenge";
        bool activeLease = change is "same-active" or "next-while-active";
        OnlineProcessLease previous = new(proof.OriginNodeId, Guid.NewGuid(), currentBoot ? proof.BootId : Guid.NewGuid(),
            sameChallenge ? proof.BootChallenge : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            1, proof.RecoveryVersion, activeLease ? expiry.AddTicks(1) : expiry, 1);
        RevisionImage image = scenario.State with { OnboardingContracts = scenario.State.OnboardingContracts! with
        {
            OnlineProcesses = ImmutableDictionary<Guid, OnlineProcessLease>.Empty.Add(previous.NodeId, previous)
        } };
        if (!currentBoot)
        {
            proof = proof with { ProcessEpoch = change == "epoch-skipped" ? 3 : 2 };
        }

        call = call with { Proof = VoterCredentialContinuationProofs.Sign(call.Request, proof, scenario.Crypto.PeerKey) };

        // Act
        Exception? error = Record.Exception(() => VoterCredentialContinuationProofs.Require(image, call, expiry));

        // Assert
        if (allowed)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.IsType<UnauthorizedAccessException>(error);
        }

        Assert.Same(previous, image.OnboardingContracts.OnlineProcesses[previous.NodeId]);
    }

    private static VoterCredentialProofRequest CreateCall(VoterCredentialCryptoScenario scenario)
    {
        RevisionImage image = scenario.State;
        DateTimeOffset expiry = scenario.Crypto.Template.NotAfterUtc;
        EnrollmentIssuance template = scenario.Intent.Issuance with
        {
            NotBeforeUtc = expiry.AddMinutes(-2), NotAfterUtc = expiry.AddDays(30)
        };
        VoterCredentialRequest request = new(1, image.ClusterId, image.Generation, Guid.NewGuid(),
            scenario.Intent with { Issuance = template });
        VoterCredentialProof proof = new(1, VoterCredentialContinuationProofs.SCOPE, template.NodeId,
            template.StorageGeneration, Guid.NewGuid(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            1, 0, image.Operations.MembershipVersion, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            expiry.AddSeconds(-1), string.Empty);

        return new(request, VoterCredentialContinuationProofs.Sign(request, proof, scenario.Crypto.PeerKey),
            Convert.ToBase64String(scenario.Csr));
    }

    private static RevisionImage ChangeAcceptedImage(VoterCredentialCryptoScenario scenario, string change)
    {
        RevisionImage image = scenario.State;
        ClusterOperationsImage operations = image.Operations;
        OperationalNode node = operations.Nodes[scenario.Intent.Issuance.NodeId];
        OperationalCredential credential = operations.Credentials[scenario.Intent.PreviousCredentialId];
        AcceptedEnrollmentIssuer issuer = image.OnboardingContracts!.Issuers![scenario.Crypto.Epoch];
        DateTimeOffset expiry = credential.ExpiresAtUtc;

        return change switch
        {
            "removed" => WithNode(image, node with { Removed = true }),
            "nonvoter" => WithNode(image, node with { Voting = false }),
            "storage" => WithNode(image, node with { StorageGeneration = Guid.NewGuid() }),
            "address" => WithNode(image, node with { Address = "https://other.example.test/internal/raft" }),
            "leaf-revoked" => image with { Operations = operations with
            {
                Credentials = operations.Credentials.SetItem(credential.Id, credential with { RevokedAtUtc = expiry })
            } },
            "issuer-revoked" => image with { OnboardingContracts = image.OnboardingContracts with
            {
                Issuers = image.OnboardingContracts.Issuers.SetItem(issuer.Epoch, issuer with { RevokedAtUtc = expiry })
            } },
            "membership" => image with { Operations = operations with { MembershipVersion = operations.MembershipVersion + 1 } },
            "maintenance" => image with { Operations = operations with { MaintenanceId = Guid.NewGuid() } },
            "still-valid" or "stale" => image,
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
    }

    private static RevisionImage WithNode(RevisionImage image, OperationalNode node)
    {
        return image with { Operations = image.Operations with { Nodes = image.Operations.Nodes.SetItem(node.NodeId, node) } };
    }
}
