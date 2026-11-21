using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster.Renewal;

public class VoterCredentialSnapshotTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RoundtripAndRecoveryAncestorsPreserveDurableBoundaries(int transitions)
    {
        // Arrange
        RenewalScenario scenario = new();
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
        RevisionImage history = Archived(restored);
        OnboardingSnapshot.Validate(history);

        // Assert
        Assert.Equal(bytes, StrictOnboardingJson.Encode(restored));
        Assert.Equal(scenario.Rotation, restored.OnboardingContracts!.VoterCredentialRotations![scenario.Intent.Issuance.IssueId]);
        Assert.Null(history.OnboardingContracts);
        Assert.Empty(history.Operations.Nodes);
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("boundIndex")]
    [InlineData("missingDecision")]
    [InlineData("digest")]
    [InlineData("missingBound")]
    [InlineData("orphan")]
    [InlineData("revision")]
    [InlineData("identity")]
    [InlineData("retirement")]
    [InlineData("retireProof")]
    public void CorruptionIsRejectedInCurrentAndArchivedState(string mutation)
    {
        // Arrange
        RenewalScenario scenario = new();
        scenario.Commit("AuthorizeVoterCredential");
        scenario.Commit("BindVoterCredential");
        scenario.Commit("RetireOldCredential");
        RevisionImage image = scenario.State;
        VoterCredentialRotation rotation = scenario.Rotation;
        rotation = mutation switch
        {
            "deadline" => rotation with { OverlapUntilUtc = rotation.OverlapUntilUtc!.Value.AddTicks(1) },
            "boundIndex" => rotation with { CredentialRevision = rotation.AuthorizedIndex },
            "digest" => rotation with { AuthorizationDigest = new string('F', 64) },
            "missingBound" => rotation with { BoundAtUtc = null },
            "orphan" => rotation with { CredentialRevision = 0, BoundAtUtc = null, BindDigest = null, BindOperationId = null, OverlapUntilUtc = null,
                RetireOperationId = null, RetiredIndex = null, RetiredAtUtc = null, RetireDigest = null, InstallationProofDigest = null },
            "revision" => rotation with { Renewal = rotation.Renewal with { PreviousCredentialRevision = 1 } },
            "identity" => rotation with { Renewal = rotation.Renewal with { Issuance = rotation.Renewal.Issuance with { StorageGeneration = Guid.NewGuid() } } },
            "retirement" => rotation with { RetiredIndex = rotation.CredentialRevision },
            "retireProof" => rotation with { InstallationProofDigest = null },
            _ => rotation
        };
        image = image with { OnboardingContracts = image.OnboardingContracts! with
        {
            VoterCredentialRotations = image.OnboardingContracts.VoterCredentialRotations!.SetItem(scenario.Intent.Issuance.IssueId, rotation)
        } };
        if (mutation == "missingDecision")
        {
            image = image with { Results = image.Results.Remove(rotation.AuthorizationOperationId) };
        }

        // Act
        Exception? currentFailure = Record.Exception(() => OnboardingSnapshot.Validate(image));
        Exception? archivedFailure = Record.Exception(() => OnboardingSnapshot.Validate(Archived(image)));

        // Assert
        Assert.NotNull(currentFailure);
        Assert.NotNull(archivedFailure);
    }

    [Fact]
    public void LaterRevocationDoesNotRewriteOrInvalidateHistoricalRotation()
    {
        // Arrange
        RenewalScenario scenario = new();
        scenario.Commit("AuthorizeVoterCredential");
        scenario.Commit("BindVoterCredential");
        OperationalCredential current = scenario.State.Operations.Credentials[scenario.Intent.Issuance.IssueId];
        RevisionImage image = scenario.State with { Operations = scenario.State.Operations with
        {
            Credentials = scenario.State.Operations.Credentials.SetItem(current.Id, current with { RevokedAtUtc = scenario.Now })
                .SetItem(scenario.Old.Id, scenario.Old with { RevokedAtUtc = scenario.Now })
        } };
        AcceptedEnrollmentIssuer issuer = image.OnboardingContracts!.Issuers!.Values.Single();
        image = image with { OnboardingContracts = image.OnboardingContracts with
        {
            Issuers = image.OnboardingContracts.Issuers.SetItem(issuer.Epoch, issuer with { RevokedAtUtc = scenario.Now })
        } };

        // Act
        OnboardingSnapshot.Validate(image);
        bool active = VoterCredentialPolicy.IsUsable(image, image.Operations.Credentials[current.Id], scenario.Now);

        // Assert
        Assert.False(active);
        Assert.Equal(scenario.Rotation, image.OnboardingContracts!.VoterCredentialRotations![scenario.Intent.Issuance.IssueId]);
    }

    internal static RevisionImage Archived(RevisionImage image)
    {
        RecoveryBaseline baseline = new(Guid.NewGuid(), image.ClusterId, image.Generation, image.Index, image.Term,
            new string('A', 64), image.Index, Guid.NewGuid(), Guid.NewGuid(), new string('B', 64), DateTimeOffset.UnixEpoch,
            image.Operations) { PreviousOnboardingContracts = image.OnboardingContracts };
        RecoveryBaseline parent = new(Guid.NewGuid(), Guid.NewGuid(), 1, image.Index, image.Term, new string('C', 64),
            image.Index, Guid.NewGuid(), Guid.NewGuid(), new string('D', 64), DateTimeOffset.UnixEpoch, new())
        {
            Ancestors = [baseline]
        };
        return new(Guid.NewGuid(), 3) { Index = image.Index, Term = image.Term, Results = image.Results, RecoveryBaseline = parent };
    }
}
