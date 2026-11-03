using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class RecoveryProofTests
{
    [Fact]
    public void FreshBootstrapHasNoOfflineIsolationOrBackupAuthority()
    {
        // Arrange
        using EnrollmentTrustFixture fixture = new();
        string challenge = Convert.ToBase64String(new byte[32]);
        RecoveryAdmission proof = new(fixture.Genesis.ClusterId, fixture.Genesis.Generation, fixture.Genesis.NodeId,
            fixture.Genesis.StorageGeneration, false, false, null, null, 0, 0)
        {
            BootstrapAccount = fixture.Account,
            Proof = new FreshBootstrapProof(fixture.Genesis, fixture.Initialize, Guid.NewGuid(), challenge)
        };

        // Act
        bool bootstrap = RecoveryProofValidation.CanBootstrap(proof);
        bool physicalIsolation = RecoveryProofValidation.CanRestore(proof with { FencingConfirmed = true });
        bool backup = RecoveryProofValidation.CanRestore(proof with { OldBackup = true });
        bool foreign = RecoveryProofValidation.CanRestore(proof with { NodeId = Guid.NewGuid() });

        // Assert
        Assert.True(bootstrap);
        Assert.False(physicalIsolation);
        Assert.False(backup);
        Assert.False(foreign);
    }

    [Fact]
    public void JoinResumeAndRestartCannotBootstrapAccountOrUseGeneralFencingBoolean()
    {
        // Arrange
        using EnrollmentTrustFixture fixture = new();
        string challenge = Convert.ToBase64String(new byte[32]);
        RecoveryAdmission proof = new(fixture.Genesis.ClusterId, fixture.Genesis.Generation, fixture.Genesis.NodeId,
            fixture.Genesis.StorageGeneration, false, false, Guid.NewGuid(), new string('A', 64), 10, 1);
        RecoveryProof[] online =
        [
            new FreshJoinProof(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), challenge, 1, 10),
            new OnboardingResumeProof(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), challenge, 2, 11),
            new ManagedRestartProof(Guid.NewGuid(), Guid.NewGuid(), challenge, new string('A', 64))
        ];

        // Act
        bool[] restore = online.Select(kind => RecoveryProofValidation.CanRestore(proof with { Proof = kind })).ToArray();
        bool[] bootstrap = online.Select(kind => RecoveryProofValidation.CanBootstrap(proof with { Proof = kind, BootstrapAccount = fixture.Account })).ToArray();
        bool unknown = RecoveryProofValidation.CanRestore(proof with { FencingConfirmed = true });
        bool explicitLegacy = RecoveryProofValidation.CanRestore(proof with { FencingConfirmed = true }, legacyOffline: true);

        // Assert
        Assert.All(restore, Assert.True);
        Assert.All(bootstrap, Assert.False);
        Assert.False(unknown);
        Assert.True(explicitLegacy);
    }
}
