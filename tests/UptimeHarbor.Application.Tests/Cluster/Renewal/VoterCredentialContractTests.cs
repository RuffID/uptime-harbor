using System.Text;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster.Renewal;

public class VoterCredentialContractTests
{
    [Theory]
    [InlineData("actor")]
    [InlineData("source")]
    [InlineData("schema")]
    [InlineData("entity")]
    [InlineData("base")]
    [InlineData("epoch")]
    [InlineData("missing")]
    [InlineData("onboarding")]
    [InlineData("online")]
    [InlineData("value")]
    [InlineData("issuer")]
    [InlineData("reauth")]
    [InlineData("sameKey")]
    [InlineData("lifetime")]
    [InlineData("credential")]
    [InlineData("proof")]
    public void InvalidEnvelopeNeverEncodes(string mutation)
    {
        // Arrange
        RenewalScenario scenario = new();
        RevisionCommand command = scenario.Command("AuthorizeVoterCredential");
        EnrollmentTrustCommand data = command.EnrollmentTrust!;
        command = mutation switch
        {
            "actor" => command with { ActorUserId = Guid.NewGuid() },
            "source" => command with { SystemSource = "OnboardingCoordinator" },
            "schema" => command with { Schema = 2 },
            "entity" => command with { EntityId = Guid.NewGuid() },
            "base" => command with { BaseRevision = 1 },
            "epoch" => command with { EnrollmentTrust = data with { IssuerEpoch = Guid.NewGuid() } },
            "missing" => command with { EnrollmentTrust = data with { Rotation = null } },
            "onboarding" => command with { Onboarding = new() { StorageGeneration = scenario.Storage, InvitationId = Guid.NewGuid() } },
            "online" => command with { OnlineLifecycle = new(1, scenario.Storage, null!) },
            "value" => command with { Value = new() },
            "issuer" => command with { EnrollmentTrust = data with { Issuer = scenario.State.OnboardingContracts!.Issuers!.Values.Single() } },
            "reauth" => command with { EnrollmentTrust = data with { ReauthenticatedAtUtc = scenario.Now } },
            "sameKey" => WithRenewal(command, scenario.Intent with { PreviousSpkiDigest = scenario.Intent.Issuance.SpkiDigest }),
            "lifetime" => WithRenewal(command, scenario.Intent with { Issuance = scenario.Intent.Issuance with { NotAfterUtc = scenario.Now.AddDays(31) } }),
            "credential" => command with { EnrollmentTrust = data with { Rotation = data.Rotation! with { Credential = scenario.Old } } },
            _ => command with { EnrollmentTrust = data with { Rotation = data.Rotation! with { InstallationProofDigest = new string('A', 64) } } }
        };

        // Act
        Exception? failure = Record.Exception(() => RevisionCodec.Encode(command));

        // Assert
        Assert.NotNull(failure);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("null")]
    public void StrictDecodeRejectsMalformedRotationJson(string mutation)
    {
        // Arrange
        RenewalScenario scenario = new();
        string json = Encoding.UTF8.GetString(RevisionCodec.Encode(scenario.Command("AuthorizeVoterCredential")));
        if (mutation == "unknown")
        {
            json = json.Replace("\"Rotation\":{", "\"Rotation\":{\"unexpected\":true,");
        }
        else if (mutation == "duplicate")
        {
            json = json.Replace("\"PreviousCredentialRevision\":0", "\"PreviousCredentialRevision\":0,\"PreviousCredentialRevision\":0");
        }
        else
        {
            json = json.Replace("\"Renewal\":{", "\"Renewal\":null,\"ignored\":{");
        }

        // Act
        Exception? failure = Record.Exception(() => RevisionCodec.Decode(Encoding.UTF8.GetBytes(json)));

        // Assert
        Assert.NotNull(failure);
    }

    [Fact]
    public void ChangingIntentUnderAcceptedOperationIdIsRejected()
    {
        // Arrange
        RenewalScenario scenario = new();
        RevisionCommand command = scenario.Commit("AuthorizeVoterCredential");
        RevisionCommand changed = WithRenewal(command, scenario.Intent with
        {
            Issuance = scenario.Intent.Issuance with { CsrDigest = new string('F', 64) }
        });

        // Act
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => scenario.Apply(changed));

        // Assert
        Assert.Equal("IdempotencyKeyReuse", failure.Message);
        Assert.Equal(scenario.Intent, scenario.Rotation.Renewal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrWrongTermStampCannotDriveReducer(bool wrongTerm)
    {
        // Arrange
        RenewalScenario scenario = new();
        RevisionCommand command = scenario.Command("AuthorizeVoterCredential");
        EnrollmentTrustCommand data = command.EnrollmentTrust! with { CoordinatorAtUtc = null, CoordinatorTerm = 0 };
        if (wrongTerm)
        {
            data = data with { CoordinatorAtUtc = scenario.Now, CoordinatorTerm = scenario.State.Term + 1 };
        }

        // Act
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => scenario.Apply(command with { EnrollmentTrust = data }));

        // Assert
        Assert.Equal("EnrollmentTrustAcceptanceUnconfirmed", failure.Message);
        Assert.Null(scenario.State.OnboardingContracts!.VoterCredentialRotations);
    }

    [Fact]
    public void LegacyBytesOmitNewOptionalProperties()
    {
        // Arrange
        RevisionScenario scenario = new();
        RevisionCommand command = scenario.Server();
        RevisionImage image = scenario.State;

        // Act
        string commandJson = Encoding.UTF8.GetString(RevisionCodec.Encode(command));
        string imageJson = Encoding.UTF8.GetString(StrictOnboardingJson.Encode(image with { OnboardingContracts = new() }));

        // Assert
        Assert.DoesNotContain("Rotation", commandJson);
        Assert.DoesNotContain("VoterCredentialRotations", imageJson);
        Assert.Equal(command, RevisionCodec.Decode(RevisionCodec.Encode(command)));
    }

    internal static RevisionCommand WithRenewal(RevisionCommand command, VoterCredentialRenewal renewal)
    {
        return command with { EnrollmentTrust = command.EnrollmentTrust! with
        {
            Rotation = command.EnrollmentTrust.Rotation! with { Renewal = renewal }
        } };
    }
}
