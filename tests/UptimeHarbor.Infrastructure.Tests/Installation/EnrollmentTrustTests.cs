using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class EnrollmentTrustTests
{
    [Fact]
    public void ExactGenesisAddsNodeBeforeCredentialAndSeparateAccountThenClosesPermit()
    {
        // Arrange
        using EnrollmentTrustFixture fixture = new();
        GenesisWriterPermit permit = new();
        permit.Arm(fixture.Genesis, fixture.Initialize, fixture.Account);
        RevisionReducer reducer = new();

        // Act
        bool initializeAllowed = permit.Allows(fixture.Initialize, fixture.Empty);
        bool earlyAccount = permit.Allows(fixture.Account, fixture.Empty);
        RevisionImage initialized = reducer.Apply(fixture.Empty, fixture.Initialize, 1, 1);
        bool accountAllowed = permit.Allows(fixture.Account, initialized);
        RevisionImage readyForActivation = reducer.Apply(initialized, fixture.Account, 2, 1);

        // Assert
        Assert.True(initializeAllowed);
        Assert.False(earlyAccount);
        Assert.Equal(OperationOutcome.Committed, initialized.Results[fixture.Initialize.OperationId].Outcome);
        Assert.Empty(initialized.Values);
        Assert.True(accountAllowed);
        Assert.All(initialized.Operations.Credentials.Values, credential => Assert.True(initialized.Operations.Nodes.ContainsKey(credential.NodeId)));
        Assert.False(Assert.Single(initialized.Operations.Nodes).Value.Activated);
        Assert.Equal(OperationOutcome.Committed, readyForActivation.Results[fixture.Account.OperationId].Outcome);
        Assert.False(permit.Allows(fixture.Account, readyForActivation));
        Assert.False(permit.Allows(fixture.Initialize, readyForActivation));
        Assert.Equal(0, readyForActivation.Operations.BotId);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("account")]
    [InlineData("node")]
    [InlineData("storage")]
    [InlineData("address")]
    [InlineData("anchor")]
    [InlineData("generation")]
    [InlineData("maintenance")]
    public void PermitCannotBroadenToAnotherPayloadOrIdentity(string mutation)
    {
        // Arrange
        using EnrollmentTrustFixture fixture = new();
        GenesisWriterPermit permit = new();
        permit.Arm(fixture.Genesis, fixture.Initialize, fixture.Account);
        RevisionCommand command = fixture.Initialize;
        RevisionImage image = fixture.Empty;
        switch (mutation)
        {
            case "operation": command = command with { OperationId = Guid.NewGuid() }; break;
            case "account": command = fixture.Account with { Value = fixture.Account.Value! with { Name = "other" } }; break;
            case "node": command = command with { OriginNodeId = Guid.NewGuid() }; break;
            case "storage": command = command with { EnrollmentTrust = command.EnrollmentTrust! with { StorageGeneration = Guid.NewGuid() } }; break;
            case "address": command = command with { EnrollmentTrust = command.EnrollmentTrust! with { Node = command.EnrollmentTrust.Node! with { Address = "https://other.test/internal/raft" } } }; break;
            case "anchor": image = image with { OnboardingContracts = image.OnboardingContracts! with { Genesis = fixture.Genesis with { IssuerSpkiDigest = new string('A', 64) } } }; break;
            case "generation": image = image with { Generation = 2 }; break;
            case "maintenance": image = image with { Operations = image.Operations with { MaintenanceId = Guid.NewGuid() } }; break;
        }

        // Act
        bool allowed = permit.Allows(command, image);

        // Assert
        Assert.False(allowed);
    }

    [Fact]
    public void MissingLocalGenesisCannotAuthorizeItselfAndRetryPreservesDecision()
    {
        // Arrange
        using EnrollmentTrustFixture fixture = new();
        RevisionReducer reducer = new();

        // Act
        RevisionImage rejected = reducer.Apply(fixture.Empty with { OnboardingContracts = null }, fixture.Initialize, 1, 1);
        RevisionImage accepted = reducer.Apply(fixture.Empty, fixture.Initialize, 1, 1);
        RevisionImage replay = reducer.Apply(accepted, fixture.Initialize, 2, 1);

        // Assert
        Assert.Equal(OperationOutcome.Rejected, rejected.Results[fixture.Initialize.OperationId].Outcome);
        Assert.Empty(rejected.Operations.Credentials);
        Assert.Equal(accepted.Results[fixture.Initialize.OperationId], replay.Results[fixture.Initialize.OperationId]);
        Assert.Single(replay.OnboardingContracts!.Issuers!);
        Assert.Single(replay.Operations.Nodes);
    }

    [Fact]
    public void RevocationAndRetirementAreAcceptedAndProjectedWithoutDeletingAnchor()
    {
        // Arrange
        using EnrollmentTrustFixture fixture = new();
        RevisionReducer reducer = new();
        RevisionImage accepted = fixture.Accepted();
        RevisionCommand retire = fixture.Revoke(accepted) with { Action = "RetireEnrollmentIssuer" };

        // Act
        RevisionImage retired = reducer.Apply(accepted, retire, 3, 1);
        RevisionImage revoked = reducer.Apply(retired, fixture.Revoke(retired), 4, 1);
        EnrollmentTrustSnapshot.Validate(revoked);
        EnrollmentIssuer projected = Assert.IsType<EnrollmentIssuer>(Assert.Single(EnrollmentTrustSnapshot.Changes(accepted, revoked)));

        // Assert
        Assert.Equal(fixture.Crypto.Now, projected.RetiredAtUtc);
        Assert.Equal(fixture.Crypto.Now, projected.RevokedAtUtc);
        Assert.Equal(fixture.Crypto.Epoch, projected.Id);
        Assert.Single(revoked.OnboardingContracts!.Issuers!);
        Assert.Equal(4, projected.LogIndex);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("revision")]
    [InlineData("reauth")]
    [InlineData("maintenance")]
    [InlineData("removed")]
    public void IssuerAdministrationRechecksCurrentAcceptedAuthority(string mutation)
    {
        // Arrange
        using EnrollmentTrustFixture fixture = new();
        RevisionImage image = fixture.Accepted();
        RevisionCommand revoke = fixture.Revoke(image);
        switch (mutation)
        {
            case "actor": revoke = revoke with { ActorUserId = Guid.NewGuid() }; break;
            case "revision": revoke = revoke with { ActorRevision = 1 }; break;
            case "reauth": revoke = revoke with { EnrollmentTrust = revoke.EnrollmentTrust! with { ReauthenticatedAtUtc = fixture.Crypto.Now.AddMinutes(-5).AddTicks(-1) } }; break;
            case "maintenance": image = image with { Operations = image.Operations with { MaintenanceId = Guid.NewGuid() } }; break;
            case "removed": image = image with { Operations = image.Operations with { Nodes = image.Operations.Nodes.SetItem(revoke.OriginNodeId, image.Operations.Nodes[revoke.OriginNodeId] with { Removed = true }) } }; break;
        }

        // Act
        RevisionImage result = new RevisionReducer().Apply(image, revoke, 3, 1);

        // Assert
        Assert.Equal(OperationOutcome.Rejected, result.Results[revoke.OperationId].Outcome);
        Assert.Null(result.OnboardingContracts!.Issuers![fixture.Crypto.Epoch].RevokedAtUtc);
    }

    [Fact]
    public void TrustCodecCoversBusinessBytesButExcludesOnlyCoordinatorStamp()
    {
        // Arrange
        using EnrollmentTrustFixture fixture = new();
        RevisionCommand changedStamp = fixture.Initialize with
        {
            EnrollmentTrust = fixture.Initialize.EnrollmentTrust! with { CoordinatorAtUtc = fixture.Crypto.Now.AddSeconds(1), CoordinatorTerm = 2 }
        };

        // Act
        RevisionCommand roundTrip = RevisionCodec.Decode(RevisionCodec.Encode(fixture.Initialize));
        string before = RevisionCodec.Digest(fixture.Initialize);
        string after = RevisionCodec.Digest(changedStamp);
        Exception? oldSchema = Record.Exception(() => RevisionCodec.Encode(fixture.Initialize with { Schema = 2 }));

        // Assert
        Assert.Equal(fixture.Initialize, roundTrip);
        Assert.Equal(before, after);
        Assert.NotEqual(RevisionCodec.Encode(fixture.Initialize), RevisionCodec.Encode(changedStamp));
        Assert.IsType<InvalidDataException>(oldSchema);
    }

    [Fact]
    public void TrustSnapshotRejectsForeignOwnerOrLegacyCompatibility()
    {
        // Arrange
        using EnrollmentTrustFixture fixture = new();
        RevisionImage image = fixture.Accepted();
        AcceptedEnrollmentIssuer issuer = image.OnboardingContracts!.Issuers![fixture.Crypto.Epoch];
        RevisionImage corrupt = image with
        {
            OnboardingContracts = image.OnboardingContracts with { Issuers = ImmutableDictionary<Guid, AcceptedEnrollmentIssuer>.Empty.Add(issuer.Epoch, issuer with { NodeId = Guid.NewGuid() }) }
        };

        // Act
        Exception? missing = Record.Exception(() => EnrollmentTrustSnapshot.Validate(corrupt));
        Exception? old = Record.Exception(() => OnboardingProtocol.ValidateSnapshot(image, new() { Mode = "Restart", ProtocolMajor = 2, CommandSchema = 2, SnapshotFormat = 2, SqlSchema = 2 }));

        // Assert
        Assert.IsType<InvalidDataException>(missing);
        Assert.IsType<InvalidDataException>(old);
    }

    [Fact]
    public void RealIssuerAndLeafPassWhileForgedAnchorOrWrongLeafFailBeforeAcceptance()
    {
        // Arrange
        using EnrollmentTrustFixture fixture = new();
        using EnrollmentTrustFixture foreign = new();
        EnrollmentTrustCommand data = fixture.Initialize.EnrollmentTrust!;

        // Act
        Exception? valid = Record.Exception(() => EnrollmentTrustValidation.Validate(fixture.Initialize, fixture.Crypto.Now));
        Exception? wrongIssuer = Record.Exception(() => EnrollmentTrustValidation.Validate(fixture.Initialize with
        {
            EnrollmentTrust = data with { Issuer = foreign.Initialize.EnrollmentTrust!.Issuer! with { Epoch = data.IssuerEpoch } }
        }, fixture.Crypto.Now));
        Exception? wrongLeaf = Record.Exception(() => EnrollmentTrustValidation.Validate(fixture.Initialize with
        {
            EnrollmentTrust = data with { Credential = foreign.Initialize.EnrollmentTrust!.Credential }
        }, fixture.Crypto.Now));

        // Assert
        Assert.Null(valid);
        Assert.IsType<InvalidDataException>(wrongIssuer);
        Assert.IsType<InvalidDataException>(wrongLeaf);
    }
}
