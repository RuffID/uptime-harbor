using System.Collections.Immutable;
using System.Text;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class OnboardingDataContractTests
{
    [Fact]
    public void NullRequiredFieldsAndNumericUtcFailWithoutPreservingUntrustedInput()
    {
        // Arrange
        OnboardingScenario scenario = new();
        byte[] request = StrictOnboardingJson.Encode(scenario.Request);
        string nullField = Encoding.UTF8.GetString(request).Replace("\"PeerUri\":\"" + scenario.Request.PeerUri + "\"", "\"PeerUri\":null", StringComparison.Ordinal);

        // Act
        Exception? missing = Record.Exception(() => StrictOnboardingJson.Decode<EnrollmentRequest>(Encoding.UTF8.GetBytes(nullField)));
        Exception? timestamp = Record.Exception(() => StrictOnboardingJson.Decode<DateTimeOffset>("12345"u8));

        // Assert
        Assert.IsType<InvalidDataException>(missing);
        Assert.IsType<InvalidDataException>(timestamp);
        Assert.Null(missing.InnerException);
        Assert.Null(timestamp.InnerException);
    }

    [Fact]
    public void SnapshotCannotSkipCredentialBindingForAdmittedAttempt()
    {
        // Arrange
        OnboardingScenario scenario = new();
        scenario.Admit();
        OnboardingInvitation invitation = scenario.Invitation with { Attempt = scenario.Invitation.Attempt! with { CertificateDigest = null } };
        RevisionImage corrupt = scenario.State with { Onboarding = scenario.State.Onboarding! with
        {
            Invitations = scenario.State.Onboarding.Invitations.SetItem(invitation.Id, invitation)
        } };

        // Act
        Exception? failure = Record.Exception(() => OnboardingSnapshot.Validate(corrupt));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal("OnboardingAttemptStageMismatch", failure.Message);
    }

    [Fact]
    public void GenesisAndImportFormatsRoundTripWithoutProvidingRuntimeAuthority()
    {
        // Arrange
        OnboardingScenario scenario = new();
        Guid genesisId = Guid.NewGuid();
        Guid migrationId = Guid.NewGuid();
        GenesisAuthorization genesis = new(1, genesisId, Guid.NewGuid(), scenario.State.ClusterId, 1,
            scenario.Journal.Node, scenario.Storage, "https://first.invalid/internal/raft", new string('A', 64),
            new string('B', 64), new string('C', 64), migrationId);
        ImportBaselineRef import = new(1, migrationId, genesisId, "source-cut-1", new string('A', 64), new string('B', 64), 1048576, "2");

        // Act
        GenesisAuthorization decoded = OnboardingDataCodec.Decode<GenesisAuthorization>(OnboardingDataCodec.Encode(genesis));
        ImportBaselineRef baseline = OnboardingDataCodec.Decode<ImportBaselineRef>(OnboardingDataCodec.Encode(import));
        OnboardingSnapshot.Validate(scenario.State with { OnboardingContracts = new() { Genesis = decoded, Import = baseline } });

        // Assert
        Assert.Equal(genesis, decoded);
        Assert.Equal(import, baseline);
        Assert.DoesNotContain(genesis.AccountOrImportDigest, genesis.ToString());
        Assert.Null(scenario.State.BootstrapAccountDigest);
        Assert.Null(scenario.State.RecoveryBaseline);
    }

    [Fact]
    public void SenderCutAndCoverageAreVersionedFormatsWithoutFakeDeliveryResults()
    {
        // Arrange
        Guid outageId = Guid.NewGuid();
        Guid cutId = Guid.NewGuid();
        SenderAssignment assignment = new(1, Guid.NewGuid(), 1, 123, Guid.NewGuid(), Guid.NewGuid(), 7, Guid.NewGuid());
        SenderOutage outage = new(1, outageId, 7, "Planning", DateTimeOffset.UnixEpoch, ImmutableDictionary<Guid, long>.Empty);
        SummaryCut cut = new(1, outageId, cutId, 20, 1, 2, 7, ImmutableDictionary<string, long>.Empty, new string('D', 64));
        CoverageReceipt receipt = new(1, Guid.NewGuid(), outageId, cutId, Guid.NewGuid(), 3, 10, 20, [Guid.NewGuid()], [Guid.NewGuid()]);

        // Act
        SenderAssignment sender = OnboardingDataCodec.Decode<SenderAssignment>(OnboardingDataCodec.Encode(assignment));
        SenderOutage decodedOutage = OnboardingDataCodec.Decode<SenderOutage>(OnboardingDataCodec.Encode(outage));
        SummaryCut decodedCut = OnboardingDataCodec.Decode<SummaryCut>(OnboardingDataCodec.Encode(cut));
        CoverageReceipt decodedReceipt = OnboardingDataCodec.Decode<CoverageReceipt>(OnboardingDataCodec.Encode(receipt));

        // Assert
        Assert.Equal(assignment, sender);
        Assert.Equal(outage.OutageId, decodedOutage.OutageId);
        Assert.Equal(cut.Index, decodedCut.Index);
        Assert.Equal(receipt.PartIds, decodedReceipt.PartIds);
        Assert.Equal(receipt.AcceptedResultIds, decodedReceipt.AcceptedResultIds);
        Assert.Throws<InvalidDataException>(() => OnboardingDataCodec.Encode(receipt with { AcceptedResultIds = [] }));
    }

    [Fact]
    public void LegacyPartitionDigestAndUnknownVersionsAreRejected()
    {
        // Arrange
        byte[] payload = "[]"u8.ToArray();
        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        LegacyHistory history = new(1, Guid.NewGuid(), Guid.NewGuid(), "Outbox", "cut", 0, digest, payload);

        // Act
        LegacyHistory decoded = OnboardingDataCodec.Decode<LegacyHistory>(OnboardingDataCodec.Encode(history));
        Exception? changed = Record.Exception(() => OnboardingDataCodec.Encode(history with { PayloadDigest = new string('A', 64) }));
        Exception? version = Record.Exception(() => OnboardingDataCodec.Encode(history with { Version = 2 }));

        // Assert
        Assert.Equal(payload, decoded.CanonicalPayload);
        Assert.IsType<InvalidDataException>(changed);
        Assert.IsType<InvalidDataException>(version);
    }

    [Fact]
    public void StrictCodecRejectsDuplicateUnknownMissingAndOversizedFieldsWithoutLeakingInput()
    {
        // Arrange
        string secret = "sensitive-untrusted-field";
        byte[][] invalid =
        [
            Encoding.UTF8.GetBytes("{\"Version\":1,\"Version\":2}"),
            Encoding.UTF8.GetBytes("{\"" + secret + "\":true}"),
            "{}"u8.ToArray(),
            new byte[262145]
        ];

        // Act
        Exception?[] failures = invalid.Select(bytes => Record.Exception(() => OnboardingDataCodec.Decode<GenesisAuthorization>(bytes))).ToArray();

        // Assert
        Assert.All(failures, failure =>
        {
            Assert.IsType<InvalidDataException>(failure);
            Assert.Null(failure.InnerException);
            Assert.DoesNotContain(secret, failure.ToString());
        });
    }

    [Fact]
    public void LocalReserveDeletesProtectedCodeAndKeepsStableOperationIds()
    {
        // Arrange
        OnboardingScenario scenario = new();
        LocalOnboardingAttempt local = new(scenario.Request.AttemptId, scenario.Request, EnrollmentRequestCodec.Digest(scenario.Request),
            "local-key-ref", 1, ImmutableDictionary<string, Guid>.Empty.Add("Reserve", Guid.NewGuid()), "encrypted-code", null);

        // Act
        LocalOnboardingAttempt reserved = local.Reserved(8);

        // Assert
        Assert.Null(reserved.ProtectedInvitation);
        Assert.Equal(local.Request, reserved.Request);
        Assert.Equal(local.OperationIds, reserved.OperationIds);
        Assert.Equal(2, reserved.Revision);
        Assert.Equal(8, reserved.ReservationIndex);
        Assert.DoesNotContain("encrypted-code", local.ToString());
    }

    [Fact]
    public void RandomCodesHaveUuidVersionFourAndRfcVariant()
    {
        // Arrange
        HashSet<Guid> codes = [];

        // Act
        foreach (int unused in Enumerable.Range(0, 128))
        {
            codes.Add(InvitationSecrets.CreateCode());
        }

        // Assert
        Assert.Equal(128, codes.Count);
        Assert.All(codes, code =>
        {
            Assert.Equal(4, code.Version);
            Assert.Equal(0x80, code.ToByteArray(bigEndian: true)[8] & 0xc0);
        });
    }
}
