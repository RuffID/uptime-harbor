using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class ManagedRestartTests
{
    [Theory]
    [InlineData("Disabled")]
    [InlineData("Bootstrap")]
    [InlineData("Join")]
    [InlineData("")]
    [InlineData("restart")]
    [InlineData("Restart ")]
    [InlineData("Unknown")]
    public async Task NonRestartModesRejectBeforeAdmissionTlsOrStorageAccess(string mode)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);
        first.Options.Mode = mode;
        LocalInstallationProfile profile = restart.Installation.Current!;
        string sealPath = Path.Combine(first.Local.StateDirectory, "managed-storage.seal");
        string manifestPath = Path.Combine(first.Options.StoragePath, "manifest.json");
        byte[] originalSeal = File.ReadAllBytes(sealPath);
        byte[] originalManifest = File.ReadAllBytes(manifestPath);
        string[] originalFiles = Directory.GetFiles(first.Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray();

        // Act
        bool prepared = await restart.Preparation.PrepareAsync(profile, token);

        // Assert
        Assert.False(prepared);
        Assert.Null(restart.Admission.Read());
        Assert.Null(restart.Tls.Certificate);
        Assert.Same(profile, restart.Installation.Current);
        Assert.Same(profile, first.Store.Profile);
        Assert.Equal(0, restart.Native.Barriers);
        Assert.Null(restart.Native.Store);
        Assert.False(restart.Installation.CommonOpen);
        Assert.False(restart.Enable.Allows());
        Assert.Equal(originalSeal, File.ReadAllBytes(sealPath));
        Assert.Equal(originalManifest, File.ReadAllBytes(manifestPath));
        Assert.Equal(originalFiles, Directory.GetFiles(first.Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray());

        // Arrange
        File.Delete(sealPath);

        // Act
        bool missingStorage = await restart.Preparation.PrepareAsync(profile, token);

        // Assert
        Assert.False(missingStorage);
        Assert.Null(restart.Admission.Read());
        Assert.Null(restart.Tls.Certificate);
        Assert.False(File.Exists(sealPath));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("seal")]
    [InlineData("missing-wal")]
    [InlineData("maintenance")]
    [InlineData("unknown-write")]
    public async Task DamagedOrOfflineStorageCannotBecomeManagedRestart(string damage)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);
        string storage = first.Options.StoragePath;
        switch (damage)
        {
            case "manifest":
                File.AppendAllText(Path.Combine(storage, "manifest.json"), " ");
                break;
            case "seal":
                File.WriteAllText(Path.Combine(first.Local.StateDirectory, "managed-storage.seal"), "corrupt");
                break;
            case "missing-wal":
                File.Delete(Path.Combine(storage, "wal", "state"));
                break;
            case "maintenance":
                File.WriteAllText(Path.Combine(first.Local.StateDirectory, "maintenance"), "offline");
                break;
            case "unknown-write":
                File.WriteAllText(Path.Combine(first.Local.StateDirectory, "managed-storage.seal.next"), "unknown");
                break;
        }

        // Act
        Exception? failure = await Record.ExceptionAsync(() => restart.Preparation.PrepareAsync(restart.Installation.Current!, token));

        // Assert
        Assert.NotNull(failure);
        Assert.Null(restart.Admission.Read());
        Assert.Equal(0, restart.Native.Barriers);
        Assert.False(restart.Installation.CommonOpen);
        Assert.False(restart.Enable.Allows());
    }

    [Fact]
    public async Task WholeSealedStorageRestoresBeforeMajorityThenNeedsNewProcessAndEnable()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        Assert.True(first.Seals!.Exists);
        OnlineGrant old = first.Enable.Current!;
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);
        restart.Native.Majority = false;

        // Act
        await restart.RestoreAsync(token);

        // Assert
        Assert.IsType<ManagedRestartProof>(restart.Admission.Read()!.Proof);
        Assert.NotNull(restart.Tls.Certificate);
        Assert.False(restart.Admission.Read()!.FencingConfirmed);
        Assert.Null(restart.Admission.Read()!.BootstrapAccount);
        Assert.Equal(0, restart.Native.Barriers);
        Assert.True(restart.Runtime.Accepted.CaughtUp);
        Assert.False(restart.Installation.CommonOpen);
        Assert.False(restart.Enable.Confirm(old));
        Assert.NotEqual(old.BootChallenge, restart.Lease.BootChallenge);

        // Act
        Exception? unavailable = await Record.ExceptionAsync(() => restart.Driver.TickAsync(token));
        restart.Native.Majority = true;
        first.Time.Now = first.Time.Now.AddMinutes(3);
        await restart.RefreshAsync(token);
        for (int step = 0; step < 5; step++)
        {
            await restart.Driver.TickAsync(token);
        }

        // Assert
        Assert.IsType<InvalidOperationException>(unavailable);
        Assert.Equal(InstallationPhase.ClusterReady, restart.Installation.Current!.Phase);
        Assert.True(restart.Installation.CommonOpen);
        Assert.True(restart.Enable.Allows());
        Assert.Equal(old.ProcessEpoch + 1, restart.Enable.Current!.ProcessEpoch);
        Assert.Equal(restart.Lease.BootChallenge, restart.Enable.Current.BootChallenge);
        Assert.NotEqual(old.GrantId, restart.Enable.Current.GrantId);
        Assert.True(first.Store.Account.Disabled);
        Assert.Single(restart.Accepted.Current!.Values);
    }
}
