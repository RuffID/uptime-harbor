using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class SetupPreflightTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.100.100.200", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00:ec2::254", false)]
    [InlineData("10.2.3.4", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("8.8.8.8", true)]
    public void PeerAddressPolicyRejectsLocalMetadataAndUnapprovedPrivateAddresses(string value, bool expected)
    {
        // Arrange
        SetupPeerAddressPolicy policy = new(new InstallationOptions { AllowedPeerNetworks = ["10.0.0.0/8"] });

        // Act
        bool actual = policy.Allows(System.Net.IPAddress.Parse(value));

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task EverySharedAndLocalQueueTableRejectsNonemptyInstallationWithoutChanges()
    {
        // Arrange
        using InstallationFixture fixture = new();
        using UptimeHarborDbContext context = new(new DbContextOptionsBuilder<UptimeHarborDbContext>().UseSqlServer().Options);
        HashSet<string> local = ["local_setup_accounts", "local_installation_profiles", "local_onboarding_attempts", "local_attempt_steps", "data_protection_keys"];
        string[] tables = context.Model.GetEntityTypes().Select(item => item.GetTableName()!).Where(item => !local.Contains(item)).ToArray();

        // Act
        foreach (string table in tables)
        {
            fixture.InventoryState.Tables[table] = true;
            ServiceResult rejected = await fixture.Preflight.CheckAsync(null, CancellationToken.None);

            // Assert
            Assert.False(rejected.Success);
            Assert.Equal("NonemptyInstallationUseMigration03a03b", rejected.Error!.Message);
            Assert.Null(fixture.Store.Profile);
            Assert.Equal(0, fixture.Store.Commits);
            fixture.InventoryState.Tables.Clear();
        }

        Assert.True(tables.Length > 20);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task SchemaEndpointOldStorageAndForeignInstallIdentityFailClosed()
    {
        // Arrange
        using InstallationFixture fixture = new();
        fixture.InventoryState.Current = false;

        // Act
        ServiceResult schema = await fixture.Preflight.CheckAsync(null, CancellationToken.None);
        fixture.InventoryState.Current = true;
        SetupPreflight noEndpoint = new(fixture.InventoryState, Options.Create(fixture.Options),
            Options.Create(fixture.Cluster), new(false, fixture.Options.PeerEndpoint), fixture.Lease);
        ServiceResult endpoint = await noEndpoint.CheckAsync(null, CancellationToken.None);
        ServiceResult foreignIdentity = await fixture.Preflight.CheckAsync(new(Guid.NewGuid(), "Setup",
            UptimeHarbor.Application.Installation.InstallationPhase.SetupReady, 1, 1, 1, fixture.Lease.BootId, fixture.Time.Now), CancellationToken.None);
        File.WriteAllText(Path.Combine(fixture.Cluster.StoragePath, "manifest.json"), "old");
        ServiceResult storage = await fixture.Preflight.CheckAsync(null, CancellationToken.None);

        // Assert
        Assert.Equal("SchemaPreparationRequired", schema.Error!.Message);
        Assert.Equal("PreparedDirectPeerEndpointRequired", endpoint.Error!.Message);
        Assert.Equal("InstallationIdentityMismatch", foreignIdentity.Error!.Message);
        Assert.Equal("ExistingClusterStorageForbidden", storage.Error!.Message);
        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.Cluster.StoragePath, "manifest.json")));
        Assert.Null(fixture.Store.Account);
    }

    [Fact]
    public void SecondInstanceCannotAcquireSameInstallationVolumes()
    {
        // Arrange
        using InstallationFixture fixture = new();

        // Act
        Exception? error = Record.Exception(() => new InstallationLease(fixture.Options, fixture.Cluster.StoragePath));

        // Assert
        Assert.IsType<IOException>(error);
        Assert.NotEqual(Guid.Empty, fixture.Lease.InstallIdentity);
    }
}
