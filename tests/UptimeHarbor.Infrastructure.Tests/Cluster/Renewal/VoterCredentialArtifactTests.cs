using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

public class VoterCredentialArtifactTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RealDerSurvivesSnapshotAndRecoveryWithoutCurrentWallClock(int transitions)
    {
        // Arrange
        using VoterCredentialCryptoScenario scenario = new();
        scenario.Commit("AuthorizeVoterCredential");
        if (transitions >= 2)
        {
            scenario.Commit("BindVoterCredential");
        }

        if (transitions == 3)
        {
            scenario.Commit("RetireOldCredential");
        }

        byte[] bytes = StrictOnboardingJson.Encode(scenario.State);

        // Act
        RevisionImage restored = StrictOnboardingJson.Decode<RevisionImage>(bytes);
        OnboardingSnapshot.Validate(restored);
        VoterCredentialArtifacts.ValidateSnapshot(restored);
        RevisionImage archived = scenario.Archived();
        OnboardingSnapshot.Validate(archived);
        VoterCredentialArtifacts.ValidateSnapshot(archived);

        // Assert
        Assert.Equal(bytes, StrictOnboardingJson.Encode(restored));
        Assert.Equal(transitions >= 2, restored.Operations.Credentials.ContainsKey(scenario.Intent.Issuance.IssueId));
        Assert.Empty(archived.Operations.Credentials);
    }

    [Theory]
    [InlineData("oldSpki")]
    [InlineData("oldExpiry")]
    [InlineData("newSpki")]
    [InlineData("serial")]
    [InlineData("lifetime")]
    [InlineData("root")]
    [InlineData("der")]
    public void ActualCryptoRejectsChangedArtifactsIncludingArchivedHistory(string mutation)
    {
        // Arrange
        using VoterCredentialCryptoScenario scenario = new();
        scenario.Commit("AuthorizeVoterCredential");
        scenario.Commit("BindVoterCredential");
        OnboardingSnapshot.Validate(scenario.State);
        VoterCredentialArtifacts.ValidateSnapshot(scenario.State);
        Guid id = scenario.Intent.Issuance.IssueId;
        VoterCredentialRotation rotation = scenario.State.OnboardingContracts!.VoterCredentialRotations![id];
        if (mutation is "oldSpki" or "newSpki" or "serial" or "lifetime")
        {
            VoterCredentialRenewal renewal = rotation.Renewal;
            if (mutation == "oldSpki")
            {
                renewal = renewal with { PreviousSpkiDigest = new string('A', 64) };
            }
            else
            {
                EnrollmentIssuance template = renewal.Issuance;
                template = mutation switch
                {
                    "newSpki" => template with { SpkiDigest = new string('B', 64) },
                    "serial" => template with { Serial = "1123456789ABCDEF0123456789ABCDEF" },
                    _ => template with { NotAfterUtc = template.NotAfterUtc.AddSeconds(-1) }
                };
                renewal = renewal with { Issuance = template };
            }

            scenario.State = scenario.State with { OnboardingContracts = scenario.State.OnboardingContracts with
            {
                VoterCredentialRotations = scenario.State.OnboardingContracts.VoterCredentialRotations.SetItem(id, rotation with { Renewal = renewal })
            } };
        }
        else if (mutation == "root")
        {
            AcceptedEnrollmentIssuer issuer = scenario.State.OnboardingContracts.Issuers![scenario.Crypto.Epoch];
            scenario.State = scenario.State with { OnboardingContracts = scenario.State.OnboardingContracts with
            {
                Issuers = scenario.State.OnboardingContracts.Issuers.SetItem(issuer.Epoch, issuer with { SpkiDigest = new string('C', 64) })
            } };
        }
        else
        {
            OperationalCredential old = scenario.State.Operations.Credentials[scenario.Intent.PreviousCredentialId];
            OperationalCredential updated = old with { ExpiresAtUtc = old.ExpiresAtUtc.AddSeconds(1) };
            if (mutation == "der")
            {
                updated = old with { PublicCertificate = Convert.ToBase64String(scenario.Next.RawData) };
            }

            scenario.State = scenario.State with { Operations = scenario.State.Operations with
            {
                Credentials = scenario.State.Operations.Credentials.SetItem(old.Id, updated)
            } };
        }

        // Act
        Exception? active = Record.Exception(() => VoterCredentialArtifacts.ValidateSnapshot(scenario.State));
        Exception? history = Record.Exception(() => VoterCredentialArtifacts.ValidateSnapshot(scenario.Archived()));

        // Assert
        Assert.NotNull(active);
        Assert.NotNull(history);
    }

    [Fact]
    public void RotationOnlySnapshotStillRequiresTrustV3IncludingAncestors()
    {
        // Arrange
        using VoterCredentialCryptoScenario scenario = new();
        scenario.Commit("AuthorizeVoterCredential");
        scenario.State = scenario.State with { OnboardingContracts = scenario.State.OnboardingContracts! with { Issuers = null } };
        ClusterOptions options = new() { Mode = "Restart", ProtocolMajor = 2, CommandSchema = 2, SnapshotFormat = 2, SqlSchema = 2 };

        // Act
        Exception? current = Record.Exception(() => OnboardingProtocol.ValidateSnapshot(scenario.State, options));
        Exception? history = Record.Exception(() => OnboardingProtocol.ValidateSnapshot(scenario.Archived(), options));

        // Assert
        Assert.IsType<InvalidDataException>(current);
        Assert.IsType<InvalidDataException>(history);
    }
}
