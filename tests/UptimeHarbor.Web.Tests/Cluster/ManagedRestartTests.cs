using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class ManagedRestartTests
{
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
