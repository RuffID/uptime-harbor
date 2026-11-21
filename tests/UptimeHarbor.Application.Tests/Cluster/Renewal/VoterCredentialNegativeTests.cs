using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster.Renewal;

public class VoterCredentialNegativeTests
{
    [Theory]
    [InlineData("missingIssuer")]
    [InlineData("retiredIssuer")]
    [InlineData("revokedIssuer")]
    [InlineData("expiredIssuer")]
    [InlineData("issuerLifetime")]
    [InlineData("oldRevoked")]
    [InlineData("nonvoter")]
    [InlineData("removed")]
    [InlineData("storage")]
    [InlineData("address")]
    [InlineData("fingerprint")]
    [InlineData("maintenance")]
    [InlineData("intentFuture")]
    [InlineData("intentTooOld")]
    [InlineData("origin")]
    public void RecheckAtBothAuthorizeAndBindPreservesOldCredential(string mutation)
    {
        // Arrange
        foreach (bool binding in new[] { false, true })
        {
            RenewalScenario scenario = new();
            if (binding)
            {
                scenario.Commit("AuthorizeVoterCredential");
            }

            Mutate(scenario, mutation);
            string action = "AuthorizeVoterCredential";
            if (binding)
            {
                action = "BindVoterCredential";
            }

            RevisionCommand command = scenario.Command(action);
            if (mutation == "origin")
            {
                command = command with { OriginNodeId = Guid.NewGuid() };
            }

            if (binding && mutation is "intentFuture" or "intentTooOld")
            {
                // Уже принятый template не подменяется другим, даже если новый intent свежий.
                scenario.Intent = scenario.Intent with { Issuance = scenario.Intent.Issuance with { CsrDigest = new string('F', 64) } };
                command = scenario.Command(action);
            }

            // Act
            CommandResult result = scenario.Apply(command);

            // Assert
            Assert.Equal(OperationOutcome.Rejected, result.Outcome);
            Assert.Single(scenario.State.Operations.Credentials);
            Assert.False(scenario.State.Operations.Credentials.ContainsKey(scenario.Intent.Issuance.IssueId));
        }
    }

    [Fact]
    public void ConcurrentAuthorizationAndDifferentBindCannotReplaceFirstIntentOrDer()
    {
        // Arrange
        RenewalScenario scenario = new();
        RevisionCommand first = scenario.Commit("AuthorizeVoterCredential");
        RevisionCommand concurrent = VoterCredentialContractTests.WithRenewal(scenario.Command("AuthorizeVoterCredential"),
            scenario.Intent with { Issuance = scenario.Intent.Issuance with { IssueId = Guid.NewGuid() } });

        // Act
        CommandResult refused = scenario.Apply(concurrent);
        scenario.Commit("BindVoterCredential");
        RevisionCommand overwrite = scenario.Command("BindVoterCredential");
        byte[] other = [10, 11, 12];
        overwrite = overwrite with { EnrollmentTrust = overwrite.EnrollmentTrust! with
        {
            Rotation = overwrite.EnrollmentTrust.Rotation! with { Credential = overwrite.EnrollmentTrust.Rotation.Credential! with
            {
                Fingerprint = RenewalScenario.Hash(other), PublicCertificate = Convert.ToBase64String(other)
            } }
        } };
        CommandResult refusedBind = scenario.Apply(overwrite);

        // Assert
        Assert.Equal(OperationOutcome.Rejected, refused.Outcome);
        Assert.Equal(OperationOutcome.Rejected, refusedBind.Outcome);
        Assert.Equal(first.OperationId, scenario.Rotation.AuthorizationOperationId);
        Assert.Equal(Convert.ToBase64String([7, 8, 9]), scenario.State.Operations.Credentials[scenario.Intent.Issuance.IssueId].PublicCertificate);
    }

    private static void Mutate(RenewalScenario scenario, string mutation)
    {
        EnrollmentIssuance leaf = scenario.Intent.Issuance;
        AcceptedEnrollmentIssuer issuer = scenario.State.OnboardingContracts!.Issuers![leaf.IssuerEpoch];
        OperationalNode node = scenario.State.Operations.Nodes[scenario.Node];
        if (mutation == "missingIssuer")
        {
            scenario.State = scenario.State with { OnboardingContracts = scenario.State.OnboardingContracts with { Issuers = null } };
        }
        else if (mutation.EndsWith("Issuer", StringComparison.Ordinal) || mutation == "issuerLifetime")
        {
            issuer = mutation switch
            {
                "retiredIssuer" => issuer with { RetiredAtUtc = scenario.Now },
                "revokedIssuer" => issuer with { RevokedAtUtc = scenario.Now },
                "expiredIssuer" => issuer with { NotAfterUtc = scenario.Now },
                _ => issuer with { NotAfterUtc = leaf.NotAfterUtc.AddTicks(-1) }
            };
            scenario.State = scenario.State with { OnboardingContracts = scenario.State.OnboardingContracts with
            {
                Issuers = scenario.State.OnboardingContracts.Issuers!.SetItem(issuer.Epoch, issuer)
            } };
        }
        else if (mutation == "oldRevoked")
        {
            scenario.State = scenario.State with { Operations = scenario.State.Operations with
            {
                Credentials = scenario.State.Operations.Credentials.SetItem(scenario.Old.Id, scenario.Old with { RevokedAtUtc = scenario.Now })
            } };
        }
        else if (mutation is "nonvoter" or "removed" or "maintenance")
        {
            scenario.State = scenario.State with { Operations = scenario.State.Operations with
            {
                Nodes = scenario.State.Operations.Nodes.SetItem(node.NodeId, node with { Voting = mutation != "nonvoter", Removed = mutation == "removed" })
            } };
            if (mutation == "maintenance")
            {
                scenario.State = scenario.State with { Operations = scenario.State.Operations with { MaintenanceId = Guid.NewGuid() } };
            }
        }
        else if (mutation == "fingerprint")
        {
            scenario.Intent = scenario.Intent with { PreviousFingerprint = new string('E', 64) };
        }
        else if (mutation != "origin")
        {
            if (mutation == "storage")
            {
                leaf = leaf with { StorageGeneration = Guid.NewGuid() };
            }
            else if (mutation == "address")
            {
                leaf = leaf with { PeerUri = "https://other.invalid/internal/raft" };
            }
            else if (mutation == "intentFuture")
            {
                leaf = leaf with { NotBeforeUtc = leaf.NotBeforeUtc.AddSeconds(1), NotAfterUtc = leaf.NotAfterUtc.AddSeconds(1) };
            }
            else
            {
                leaf = leaf with { NotBeforeUtc = leaf.NotBeforeUtc.AddSeconds(-11), NotAfterUtc = leaf.NotAfterUtc.AddSeconds(-11) };
            }

            scenario.Intent = scenario.Intent with { Issuance = leaf };
        }
    }
}
