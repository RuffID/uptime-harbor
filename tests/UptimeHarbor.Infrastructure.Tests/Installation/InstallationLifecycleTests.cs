using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class InstallationLifecycleTests
{
    [Fact]
    public async Task FailedDrainDurablyBlocksTransitionAndStaleCallbackCannotContinue()
    {
        // Arrange
        using InstallationFixture fixture = new();
        await fixture.InitializeAsync();
        LocalInstallationProfile preparing = fixture.Store.Profile! with
        {
            Phase = InstallationPhase.Preparing, AttemptId = Guid.NewGuid()
        };
        fixture.Store.Profile = preparing;
        Runtime runtime = new() { Drained = false };
        InstallationLifecycle lifecycle = new(fixture.Store, fixture.Store, fixture.State, runtime,
            new UnavailableSetupAuthority(), fixture.Lease, fixture.Time, NullLogger<InstallationLifecycle>.Instance);

        // Act
        ServiceResult<SetupStatus> result = await lifecycle.AdvanceAsync(preparing.ProfileRevision,
            preparing.RuntimeGeneration, InstallationPhase.Quiescing, null, CancellationToken.None);
        ServiceResult<SetupStatus> stale = await lifecycle.AdvanceAsync(preparing.ProfileRevision,
            preparing.RuntimeGeneration, InstallationPhase.LocalPrepared, null, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.False(stale.Success);
        Assert.Equal(InstallationPhase.FailedNeedsAction, fixture.Store.Profile!.Phase);
        Assert.Equal("RuntimeDrainFailed", fixture.Store.Profile.ErrorCode);
        Assert.Equal(preparing.AttemptId, fixture.Store.Profile.AttemptId);
        Assert.Equal(preparing.RuntimeGeneration + 1, fixture.Store.Profile.RuntimeGeneration);
        Assert.Equal(0, runtime.Starts);
        Assert.False(fixture.State.CommonOpen);
    }

    [Fact]
    public async Task LocalSaveAndInventedEvidenceCannotRevokeSetupOrOpenProduction()
    {
        // Arrange
        using InstallationFixture fixture = new();
        await fixture.InitializeAsync();
        LocalInstallationProfile awaiting = fixture.Store.Profile! with
        {
            Phase = InstallationPhase.AwaitingActivation, AttemptId = Guid.NewGuid()
        };
        fixture.Store.Profile = awaiting;
        Runtime runtime = new();
        InstallationLifecycle lifecycle = new(fixture.Store, fixture.Store, fixture.State, runtime,
            new UnavailableSetupAuthority(), fixture.Lease, fixture.Time, NullLogger<InstallationLifecycle>.Instance);
        SetupAuthorityEvidence invented = new(awaiting.AttemptId.Value, awaiting.RuntimeGeneration, 123, new string('A', 64), SetupAuthorityEvidenceKind.ActivationVerified);

        // Act
        ServiceResult<SetupStatus> result = await lifecycle.AdvanceAsync(awaiting.ProfileRevision,
            awaiting.RuntimeGeneration, InstallationPhase.ClusterReady, invented, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(InstallationPhase.AwaitingActivation, fixture.Store.Profile.Phase);
        Assert.NotNull(fixture.Store.Account!.PasswordHash);
        Assert.False(fixture.Store.Account.Disabled);
        Assert.Equal(0, runtime.Starts);
        Assert.False(fixture.State.CommonOpen);
    }

    [Fact]
    public async Task LostPreparationReplyKeepsOriginalIdsAndKeyAndJoinNeverCreatesCommonAdministrator()
    {
        // Arrange
        using InstallationFixture fixture = new();
        await fixture.InitializeAsync();
        SetupSession session = (await fixture.Login.LoginAsync("local-admin", InstallationFixture.ORIGINAL_PASSWORD, CancellationToken.None)).Data!;
        InstallationPreparation preparation = new(fixture.Store, fixture.State, fixture.Login, fixture.Preflight,
            fixture.Secrets, new ForbiddenCredentials(), Options.Create(fixture.Options), fixture.Time,
            NullLogger<InstallationPreparation>.Instance, fixture.BotIdentity);
        SetupPreparationRequest request = new("Join", 1, 1);

        // Act
        ServiceResult<SetupStatus> first = await preparation.PrepareAsync(session, request, CancellationToken.None);
        LocalInstallationProfile saved = fixture.Store.Profile!;
        ServiceResult<SetupStatus> repeated = await preparation.PrepareAsync(session, request, CancellationToken.None);

        // Assert
        Assert.True(first.Success);
        Assert.True(repeated.Success);
        Assert.Equal(first.Data, repeated.Data);
        Assert.Equal(saved, fixture.Store.Profile);
        Assert.NotEqual(Guid.Empty, saved.AttemptId);
        Assert.NotEqual(Guid.Empty, saved.NodeId);
        Assert.NotEqual(Guid.Empty, saved.StorageGeneration);
        Assert.Null(saved.ClusterId);
        Assert.Null(saved.BootstrapAccountReference);
        Assert.Equal(0, saved.BotId);
        Assert.Single(fixture.Store.Steps);
        Assert.Equal(saved.RequestDigest, fixture.Store.Steps.Values.Single().RequestDigest);
        Assert.False(fixture.State.CommonOpen);
    }

    private class Runtime : IInstallationRuntime
    {
        public bool Drained { get; set; } = true;
        public int Starts { get; private set; }
        public Action? BeforePublish { get; set; }
        public Task<bool> DrainAsync(long generation, TimeSpan budget, CancellationToken token)
        {
            return Task.FromResult(Drained);
        }

        public Task StartQuarantinedAsync(LocalInstallationProfile profile, CancellationToken token)
        {
            Starts++;
            return Task.CompletedTask;
        }

        public Task PublishReadyAsync(LocalInstallationProfile profile, CancellationToken token)
        {
            BeforePublish?.Invoke();
            Starts++;
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationRevokesSetupDurablyBeforePublicationAndRuntimeFailureStaysClosed(bool fail)
    {
        // Arrange
        using InstallationFixture fixture = new();
        await fixture.InitializeAsync();
        LocalInstallationProfile awaiting = fixture.Store.Profile! with
        {
            Phase = InstallationPhase.AwaitingActivation, AttemptId = Guid.NewGuid()
        };
        awaiting = awaiting with { ProfileRevision = awaiting.ProfileRevision + 1 };
        fixture.Store.Profile = awaiting;
        fixture.State.Publish(awaiting);
        int commitsBefore = fixture.Store.Commits;
        Runtime runtime = new()
        {
            BeforePublish = () =>
            {
                Assert.True(fixture.Store.Commits > commitsBefore);
                Assert.Null(fixture.Store.Account!.PasswordHash);
                Assert.True(fixture.Store.Account.Disabled);
                Assert.Equal(awaiting.SetupEpoch + 1, fixture.Store.Profile!.SetupEpoch);
                Assert.False(fixture.State.CommonOpen);
                if (fail)
                {
                    throw new IOException("private exception details");
                }
            }
        };
        InstallationLifecycle lifecycle = new(fixture.Store, fixture.Store, fixture.State, runtime,
            new SetupAuthorityDouble { Activation = true }, fixture.Lease, fixture.Time,
            NullLogger<InstallationLifecycle>.Instance);
        SetupAuthorityEvidence evidence = new(awaiting.AttemptId.Value, awaiting.RuntimeGeneration, 20,
            new string('A', 64), SetupAuthorityEvidenceKind.ActivationVerified);

        // Act
        ServiceResult<SetupStatus> result = await lifecycle.AdvanceAsync(awaiting.ProfileRevision,
            awaiting.RuntimeGeneration, InstallationPhase.ClusterReady, evidence, CancellationToken.None);

        // Assert
        Assert.Equal(!fail, result.Success);
        Assert.Equal(!fail, fixture.State.CommonOpen);
        Assert.Null(fixture.Store.Account!.PasswordHash);
        if (fail)
        {
            Assert.Equal(InstallationPhase.FailedNeedsAction, fixture.Store.Profile!.Phase);
            Assert.Equal("RuntimeEffectUnknown", fixture.Store.Profile.ErrorCode);
        }
        else
        {
            Assert.Equal(InstallationPhase.ClusterReady, fixture.Store.Profile!.Phase);
        }
    }

    private class ForbiddenCredentials : IAccountCredentialService
    {
        public Task<ServiceResult<ProtectedCredential>> PrepareAsync(Guid userId, string login, string password, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("JoinCannotCreateAccount");
        }

        public bool Verify(Guid userId, string login, ProtectedCredential credential, string password)
        {
            throw new InvalidOperationException("JoinCannotReadCommonAccount");
        }
    }
}
