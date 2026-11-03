using UptimeHarbor.Application.Installation;
using UptimeHarbor.Application.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class SetupAccountTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrphanLocalAccountOrProgressCannotBecomeNewSetup(bool account)
    {
        // Arrange
        using InstallationFixture fixture = new();
        if (account)
        {
            fixture.Store.Account = new LocalSetupAccount { Id = Guid.NewGuid(), CreatedForInstallIdentity = Guid.NewGuid() };
        }
        else
        {
            fixture.Store.Progress = true;
        }

        // Act
        Exception? failure = await Record.ExceptionAsync(() => fixture.InitializeAsync());

        // Assert
        Assert.Equal("OrphanLocalSetupState", failure!.Message);
        Assert.Null(fixture.Store.Profile);
        Assert.Equal(0, fixture.Store.Commits);
    }

    [Fact]
    public async Task NewProcessRestoresSameIdsButInvalidatesPreviousSession()
    {
        // Arrange
        using InstallationFixture fixture = new();
        await fixture.InitializeAsync();
        SetupSession previous = (await fixture.Login.LoginAsync("local-admin", InstallationFixture.ORIGINAL_PASSWORD, CancellationToken.None)).Data!;
        Guid attempt = Guid.NewGuid();
        LocalInstallationProfile prepared = fixture.Store.Profile! with
        {
            Mode = "Join", Phase = InstallationPhase.LocalPrepared, AttemptId = attempt,
            NodeId = Guid.NewGuid(), StorageGeneration = Guid.NewGuid(),
            KeyReference = fixture.Secrets.Write(attempt, "peer", "persisted peer key"u8)
        };
        fixture.Store.Profile = prepared;
        fixture.Lease.Dispose();
        fixture.Time.Now = fixture.Time.Now.AddMinutes(3);
        using InstallationLease nextLease = new(fixture.Options, fixture.Cluster.StoragePath);
        InstallationState nextState = new();
        LocalSecretStore nextSecrets = new(Options.Create(fixture.Options), nextLease);
        SetupInitialization initialization = new(fixture.Store, fixture.Store,
            Options.Create(new BootstrapAdminOptions { Login = "changed", Password = "Different-Z9!credentials" }),
            nextLease, new(fixture.InventoryState, Options.Create(fixture.Options), Options.Create(fixture.Cluster),
                new(true, fixture.Options.PeerEndpoint), nextLease), nextState, nextSecrets, fixture.Time,
            NullLogger<SetupInitialization>.Instance);
        SetupAccountLogin nextLogin = new(fixture.Store, fixture.Store, nextLease, nextState, fixture.Time,
            NullLogger<SetupAccountLogin>.Instance);

        // Act
        await initialization.InitializeAsync(CancellationToken.None);
        bool oldSession = await nextLogin.ValidateAsync(previous, CancellationToken.None);

        // Assert
        Assert.False(oldSession);
        Assert.Equal(prepared.AttemptId, nextState.Current!.AttemptId);
        Assert.Equal(prepared.NodeId, nextState.Current.NodeId);
        Assert.Equal(prepared.StorageGeneration, nextState.Current.StorageGeneration);
        Assert.Equal(prepared.KeyReference, nextState.Current.KeyReference);
        Assert.Equal(prepared.SetupEpoch + 1, nextState.Current.SetupEpoch);
        Assert.NotEqual(prepared.BootId, nextState.Current.BootId);
        Assert.False(nextState.CommonOpen);
    }

    [Fact]
    public async Task FirstVisitorCannotCreateAccountAndConfigChangeCannotResetExistingPassword()
    {
        // Arrange
        using InstallationFixture fixture = new();

        // Act
        ServiceResult<SetupSession> visitor = await fixture.Login.LoginAsync("attacker", "New-Z9!attacker", CancellationToken.None);

        // Assert
        Assert.False(visitor.Success);
        Assert.Null(fixture.Store.Account);

        // Act
        await fixture.InitializeAsync();
        Guid accountId = fixture.Store.Account!.Id;
        string hash = fixture.Store.Account.PasswordHash!;
        await fixture.InitializeAsync("Different-Z9!credentials");
        ServiceResult<SetupSession> changed = await fixture.Login.LoginAsync("local-admin", "Different-Z9!credentials", CancellationToken.None);
        ServiceResult<SetupSession> original = await fixture.Login.LoginAsync("local-admin", InstallationFixture.ORIGINAL_PASSWORD, CancellationToken.None);

        // Assert
        Assert.False(changed.Success);
        Assert.True(original.Success);
        Assert.Equal(accountId, fixture.Store.Account.Id);
        Assert.Equal(hash, fixture.Store.Account.PasswordHash);
    }

    [Fact]
    public async Task FifthFailureLocksForFiveMinutesAndAbsoluteSessionTtlIsThirtyMinutes()
    {
        // Arrange
        using InstallationFixture fixture = new();
        await fixture.InitializeAsync();

        // Act
        foreach (int ignored in Enumerable.Range(0, 5))
        {
            await fixture.Login.LoginAsync("local-admin", "incorrect", CancellationToken.None);
        }

        ServiceResult<SetupSession> locked = await fixture.Login.LoginAsync("local-admin", InstallationFixture.ORIGINAL_PASSWORD, CancellationToken.None);

        // Assert
        Assert.False(locked.Success);
        Assert.Equal(fixture.Time.Now.AddMinutes(5), fixture.Store.Account!.LockoutUntilUtc);

        // Act
        fixture.Time.Now = fixture.Time.Now.AddMinutes(5);
        ServiceResult<SetupSession> accepted = await fixture.Login.LoginAsync("local-admin", InstallationFixture.ORIGINAL_PASSWORD, CancellationToken.None);
        bool valid = await fixture.Login.ValidateAsync(accepted.Data!, CancellationToken.None);
        fixture.Time.Now = fixture.Time.Now.AddMinutes(30);
        bool expired = await fixture.Login.ValidateAsync(accepted.Data!, CancellationToken.None);

        // Assert
        Assert.True(valid);
        Assert.False(expired);
    }

    [Fact]
    public async Task EpochStampBootAndPhaseMustAllMatch()
    {
        // Arrange
        using InstallationFixture fixture = new();
        await fixture.InitializeAsync();
        SetupSession session = (await fixture.Login.LoginAsync("local-admin", InstallationFixture.ORIGINAL_PASSWORD, CancellationToken.None)).Data!;

        // Act
        bool good = await fixture.Login.ValidateAsync(session, CancellationToken.None);
        bool wrongStamp = await fixture.Login.ValidateAsync(session with { Stamp = "other" }, CancellationToken.None);
        bool wrongEpoch = await fixture.Login.ValidateAsync(session with { Epoch = session.Epoch + 1 }, CancellationToken.None);
        bool wrongBoot = await fixture.Login.ValidateAsync(session with { BootId = Guid.NewGuid() }, CancellationToken.None);
        fixture.Store.Profile = fixture.Store.Profile! with { Phase = InstallationPhase.ClusterReady };
        bool closed = await fixture.Login.ValidateAsync(session, CancellationToken.None);

        // Assert
        Assert.True(good);
        Assert.False(wrongStamp);
        Assert.False(wrongEpoch);
        Assert.False(wrongBoot);
        Assert.False(closed);
    }
}
