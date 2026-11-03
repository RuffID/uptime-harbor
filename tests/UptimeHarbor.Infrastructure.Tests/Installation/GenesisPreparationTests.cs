using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class GenesisPreparationTests
{
    [Fact]
    public async Task DurableIntentKeepsOriginalKeyAccountIdsCsrAndDerWithoutClaimingAcceptedResult()
    {
        // Arrange
        using GenesisFixture fixture = new();
        LocalInstallationProfile original = fixture.Local.Store.Profile!;

        // Act
        SetupAuthorityEvidence evidence = await fixture.PrepareAsync();
        Assert.Equal(0, fixture.Signing.Commits);
        await fixture.SaveAsync(evidence);
        SetupAuthorityEvidence retry = await fixture.PrepareAsync();
        PreparedGenesis prepared = retry.Genesis!;

        // Assert
        Assert.Equal(0, evidence.AcceptedIndex);
        Assert.Equal(1, fixture.Signing.Commits);
        Assert.NotEqual(prepared.Authorization.GenesisId, fixture.Account.OperationId);
        Assert.Equal(original.AttemptId, prepared.Authorization.GenesisId);
        Assert.Equal(original.BootstrapAccountDigest, prepared.Authorization.AccountOrImportDigest);
        Assert.Equal(original.KeyReference, fixture.Local.Store.Profile!.KeyReference);
        Assert.Equal(original.OperationIds, fixture.Local.Store.Profile.OperationIds);
        Assert.Equal(evidence.Genesis!.CsrDer, prepared.CsrDer);
        Assert.Equal(RevisionCodec.Digest(evidence.Genesis.InitializeCluster), RevisionCodec.Digest(prepared.InitializeCluster));
        Assert.Single(fixture.Signing.Issuers);
        EnrollmentIssuedArtifact artifact = Assert.Single(fixture.Signing.Issued).Value;
        Assert.Equal(prepared.Authorization.GenesisId, artifact.GenesisId);
        Assert.Equal(0, artifact.AuthorizationIndex);
        Assert.Equal(Convert.ToBase64String(artifact.CertificateDer), prepared.InitializeCluster.EnrollmentTrust!.Credential!.PublicCertificate);
        Assert.False(File.Exists(Path.Combine(fixture.Local.Cluster.StoragePath, "manifest.json")));
        Assert.Null(fixture.Tls.Certificate);
        Assert.False(fixture.Local.State.CommonOpen);
    }

    [Fact]
    public async Task ExactLocalIntentPermitsQuarantineWithoutFencingOrGeneralWriter()
    {
        // Arrange
        using GenesisFixture fixture = new();
        SetupAuthorityEvidence evidence = await fixture.PrepareAsync();
        await fixture.SaveAsync(evidence);
        GenesisRuntimePreparation runtime = fixture.Runtime();
        LocalInstallationProfile profile = fixture.Local.Store.Profile!;

        // Act
        bool prepared = await runtime.PrepareAsync(profile, TestContext.Current.CancellationToken);
        RecoveryAdmission proof = fixture.Admission.Read()!;
        RevisionImage initial = new(profile.ClusterId!.Value, 1)
        {
            BootstrapAccountDigest = profile.BootstrapAccountDigest,
            OnboardingContracts = new() { Genesis = evidence.Genesis!.Authorization }
        };

        // Assert
        Assert.True(prepared);
        Assert.False(proof.FencingConfirmed);
        Assert.IsType<FreshBootstrapProof>(proof.Proof);
        Assert.True(RecoveryProofValidation.CanRestore(proof));
        Assert.True(RecoveryProofValidation.CanBootstrap(proof));
        Assert.True(fixture.Permit.Allows(evidence.Genesis!.InitializeCluster, initial));
        Assert.False(fixture.Permit.Allows(fixture.Account, initial));
        Assert.Empty(initial.Operations.Credentials);
        Assert.False(fixture.Accepted.Available);
        Assert.True(fixture.Tls.Certificate!.HasPrivateKey);
        Assert.False(fixture.Local.State.CommonOpen);
        Assert.Equal(fixture.Local.Lease.BootChallenge, ((FreshBootstrapProof)proof.Proof!).BootChallenge);
    }

    [Theory]
    [InlineData("journal")]
    [InlineData("orphan")]
    [InlineData("started")]
    [InlineData("identity")]
    [InlineData("csr")]
    public async Task PartialForeignOrUnknownPreparationCannotPublishCredential(string mutation)
    {
        // Arrange
        using GenesisFixture fixture = new();
        SetupAuthorityEvidence evidence = await fixture.PrepareAsync();
        if (mutation == "identity")
        {
            evidence = evidence with { Genesis = evidence.Genesis! with
            {
                Authorization = evidence.Genesis.Authorization with { StorageGeneration = Guid.NewGuid() }
            } };
        }
        else if (mutation == "csr")
        {
            evidence = evidence with { Genesis = evidence.Genesis! with { CsrDer = [1, 2, 3] } };
        }

        await fixture.SaveAsync(evidence);
        if (mutation == "journal")
        {
            fixture.Signing.Issued.Clear();
        }
        else if (mutation == "orphan")
        {
            fixture.Local.Secrets.Write(Guid.NewGuid(), "peer", "unreferenced"u8);
        }
        else if (mutation == "started")
        {
            File.WriteAllBytes(Path.Combine(fixture.Local.Cluster.StoragePath, "started"), [1]);
        }

        GenesisRuntimePreparation runtime = fixture.Runtime();

        // Act
        Exception? rejected = await Record.ExceptionAsync(() => runtime.PrepareAsync(fixture.Local.Store.Profile!, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(rejected);
        Assert.Null(fixture.Admission.Read());
        Assert.Null(fixture.Tls.Certificate);
        Assert.False(fixture.Local.State.CommonOpen);
    }

    [Fact]
    public async Task BootstrapAccountCannotPrecedeAcceptedInitializeEvenWithCorrectDigest()
    {
        // Arrange
        using GenesisFixture fixture = new();
        SetupAuthorityEvidence evidence = await fixture.PrepareAsync();
        RevisionImage initial = new(fixture.Account.ClusterId, 1)
        {
            BootstrapAccountDigest = RevisionCodec.Digest(fixture.Account),
            OnboardingContracts = new() { Genesis = evidence.Genesis!.Authorization }
        };
        RevisionReducer reducer = new();

        // Act
        RevisionImage refused = reducer.Apply(initial, fixture.Account, 1, 1);
        RevisionCommand initialize = evidence.Genesis!.InitializeCluster with
        {
            EnrollmentTrust = evidence.Genesis.InitializeCluster.EnrollmentTrust! with
            {
                CoordinatorTerm = 1, CoordinatorAtUtc = fixture.Local.Time.Now, GenesisMembershipVersion = 17
            }
        };
        RevisionImage initialized = reducer.Apply(initial, initialize, 1, 1);
        RevisionImage accepted = reducer.Apply(initialized, fixture.Account, 2, 1);

        // Assert
        Assert.Equal(OperationOutcome.Rejected, refused.Results[fixture.Account.OperationId].Outcome);
        Assert.Empty(refused.Values);
        Assert.Equal(OperationOutcome.Committed, accepted.Results[fixture.Account.OperationId].Outcome);
        Assert.Equal(17, accepted.Operations.MembershipVersion);
        Assert.Single(accepted.Values);
        Assert.False(accepted.Operations.Nodes.Values.Single().Activated);
        Assert.Equal(fixture.Account.Value!.PasswordHash, accepted.Values[fixture.Account.EntityId].Value.PasswordHash);
    }
}
