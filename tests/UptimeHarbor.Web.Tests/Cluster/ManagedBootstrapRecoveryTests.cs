using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Web.Security;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class ManagedBootstrapRecoveryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task LostCommittedReplyReusesGenesisAccountAndOnlineOperations(int lostReply)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await first.StartQuarantineAsync(token);
        LocalInstallationProfile original = first.Store.Profile!;
        first.Native.LoseResponseAt = lostReply;
        int lost = 0;

        // Act
        for (int step = 0; step < 12 && !first.Installation.CommonOpen; step++)
        {
            Exception? failure = await Record.ExceptionAsync(async () =>
            {
                await first.AccountBootstrap.InitializeAsync(token);
                await first.Driver.TickAsync(token);
            });
            if (failure is not null)
            {
                Assert.IsType<HttpRequestException>(failure);
                Assert.False(first.Installation.CommonOpen);
                Assert.False(first.Store.Account.Disabled);
                lost++;
            }
        }

        // Assert
        Assert.Equal(1, lost);
        Assert.Equal(1, first.Native.DroppedResponses);
        Assert.Equal(7, first.Native.Replications);
        Assert.True(first.Installation.CommonOpen);
        Assert.True(first.Enable.Allows());
        Assert.True(first.Store.Account.Disabled);
        Assert.Null(first.Store.Account.PasswordHash);
        Assert.Single(first.Store.Issuers);
        Assert.Single(first.Store.Issues);
        Assert.Single(first.Accepted.Current!.Values);
        Assert.Equal(first.Account.ActorUserId, first.Accepted.Current.Values.Single().Key);
        Assert.Equal(original.AttemptId, first.Store.Profile!.AttemptId);
        Assert.Equal(original.KeyReference, first.Store.Profile.KeyReference);
        Assert.Equal(original.OperationIds, first.Store.Profile.OperationIds);
        Assert.Equal(1, first.Enable.Current!.ProcessEpoch);
        Assert.False(first.Permit.Allows(first.Account, first.Accepted.Current));
        OnboardingContractSnapshot.Validate(first.Accepted.Current);
    }

    [Fact]
    public async Task SetupRevocationCommitsBeforeCommonPublicationAndSetupRoutesClose()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await first.StartQuarantineAsync(token);
        await first.AccountBootstrap.InitializeAsync(token);
        for (int step = 0; step < 4; step++)
        {
            await first.Driver.TickAsync(token);
        }

        Assert.Equal(InstallationPhase.AwaitingActivation, first.Store.Profile!.Phase);
        int committed = first.Store.Commits;
        string stamp = first.Store.Account.Stamp;
        long epoch = first.Store.Profile.SetupEpoch;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool published = false;
        first.Store.BeforeCommit = async cancellation =>
        {
            if (first.Store.Profile!.Phase == InstallationPhase.ClusterReady)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellation);
            }
        };
        first.BeforeReadyPublication = () =>
        {
            Assert.True(first.Store.Commits > committed);
            Assert.True(first.Store.Account.Disabled);
            Assert.Null(first.Store.Account.PasswordHash);
            Assert.NotEqual(stamp, first.Store.Account.Stamp);
            Assert.Equal(epoch + 1, first.Store.Profile!.SetupEpoch);
            Assert.False(first.Installation.CommonOpen);
            published = true;
        };

        // Act
        Task tick = first.Driver.TickAsync(token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        try
        {
            Assert.False(first.Installation.CommonOpen);
            Assert.False(published);
            Assert.Equal(503, await BrowserStatusAsync(first, "/api/admin/users"));
        }
        finally
        {
            release.TrySetResult();
        }

        await tick.WaitAsync(TimeSpan.FromSeconds(10), token);

        // Assert
        Assert.True(published);
        Assert.True(first.Installation.CommonOpen);
        Assert.Equal(204, await BrowserStatusAsync(first, "/api/admin/users"));
        Assert.Equal(404, await BrowserStatusAsync(first, "/api/setup/status"));
    }

    [Fact]
    public async Task LostSetupCommitReplyStaysClosedAndRestartKeepsOriginalGenesisAndAccount()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        first.Store.AfterCommit = cancellation =>
        {
            if (first.Store.Profile!.Phase == InstallationPhase.ClusterReady)
            {
                throw new IOException("TestReplyLostAfterSetupRevocationCommit");
            }

            return Task.CompletedTask;
        };

        // Act
        Exception? failure = await Record.ExceptionAsync(() => EnrollmentGatewayTests.ReadyAsync(first, token));

        // Assert
        Assert.IsType<IOException>(failure);
        Assert.Equal(InstallationPhase.ClusterReady, first.Store.Profile!.Phase);
        Assert.Equal(InstallationPhase.AwaitingActivation, first.Installation.Current!.Phase);
        Assert.False(first.Installation.CommonOpen);
        Assert.True(first.Store.Account.Disabled);
        Assert.Null(first.Store.Account.PasswordHash);
        Assert.True(first.Seals!.Exists);
        Assert.Equal(503, await BrowserStatusAsync(first, "/api/admin/users"));

        // Arrange
        first.Store.AfterCommit = null;
        Guid genesis = first.Accepted.Current!.OnboardingContracts!.Genesis!.GenesisId;
        Guid account = Assert.Single(first.Accepted.Current.Values).Key;
        int issuerCount = first.Store.Issuers.Count;
        int issueCount = first.Store.Issues.Count;
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);

        // Act
        await restart.RestoreAsync(token);
        Exception? stalePermit = await Record.ExceptionAsync(async () =>
            await restart.Writer.ReplicateAsync(RevisionCodec.Encode(first.Account), RevisionCodec.COMMAND_ID, token));

        // Assert
        Assert.IsType<InvalidOperationException>(stalePermit);
        Assert.Equal(0, restart.Native.Replications);
        Assert.False(restart.Installation.CommonOpen);

        // Act
        first.Time.Now = first.Time.Now.AddMinutes(3);
        await restart.RefreshAsync(token);
        for (int step = 0; step < 5; step++)
        {
            await restart.Driver.TickAsync(token);
        }

        // Assert
        Assert.True(restart.Installation.CommonOpen);
        Assert.True(restart.Enable.Allows());
        Assert.Equal(genesis, restart.Accepted.Current!.OnboardingContracts!.Genesis!.GenesisId);
        Assert.Equal(account, Assert.Single(restart.Accepted.Current.Values).Key);
        Assert.Equal(issuerCount, first.Store.Issuers.Count);
        Assert.Equal(issueCount, first.Store.Issues.Count);
        Assert.Null(restart.Admission.Read()!.BootstrapAccount);
        Assert.Equal(2, restart.Enable.Current!.ProcessEpoch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedUnsealedBootstrapCannotStartAnotherGenesisOrAccount(bool withAccount)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await first.StartQuarantineAsync(token);
        if (withAccount)
        {
            await first.AccountBootstrap.InitializeAsync(token);
        }

        Guid? genesis = first.Accepted.Current!.OnboardingContracts!.Genesis?.GenesisId;
        int accounts = first.Accepted.Current.Values.Count;
        int issues = first.Store.Issues.Count;
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);

        // Act
        Exception? failure = await Record.ExceptionAsync(() => restart.RestoreAsync(token));

        // Assert
        Assert.NotNull(failure);
        Assert.False(restart.Installation.CommonOpen);
        Assert.False(restart.Enable.Allows());
        Assert.Null(restart.Native.Store);
        Assert.Equal(genesis, first.Accepted.Current.OnboardingContracts.Genesis?.GenesisId);
        Assert.Equal(accounts, first.Accepted.Current.Values.Count);
        Assert.Equal(issues, first.Store.Issues.Count);
        Assert.False(first.Store.Account.Disabled);
    }

    [Fact]
    public async Task EveryBootstrapStageBeforeDurableRevocationKeepsCommonUiClosed()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await first.StartQuarantineAsync(token);

        // Act
        int beforeAccount = await BrowserStatusAsync(first, "/api/admin/users");
        await first.AccountBootstrap.InitializeAsync(token);

        // Assert
        Assert.Equal(503, beforeAccount);
        Assert.Single(first.Accepted.Current!.Values);
        Assert.NotEqual(first.Store.Account.Id, first.Accepted.Current.Values.Single().Key);
        Assert.False(first.Installation.CommonOpen);

        for (int step = 0; step < 5; step++)
        {
            // Act
            int common = await BrowserStatusAsync(first, "/api/admin/users");
            int setup = await BrowserStatusAsync(first, "/api/setup/status");

            // Assert
            Assert.Equal(503, common);
            Assert.Equal(204, setup);
            Assert.False(first.Store.Account.Disabled);
            Assert.Null(first.Accepted.Current!.Operations.Sender);

            // Act
            await first.Driver.TickAsync(token);
        }

        // Assert
        Assert.True(first.Installation.CommonOpen);
        Assert.True(first.Enable.Allows());
        Assert.Equal(204, await BrowserStatusAsync(first, "/api/admin/users"));
        Assert.Equal(404, await BrowserStatusAsync(first, "/api/setup/status"));
    }

    [Fact]
    public async Task FailedReadyPublicationDoesNotRestoreSetupOrRepeatBootstrap()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        int publications = 0;
        first.BeforeReadyPublication = () =>
        {
            publications++;
            throw new IOException("TestReadyPublicationFailed");
        };

        // Act
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        int replications = first.Native.Replications;
        await first.Driver.TickAsync(token);

        // Assert
        Assert.Equal(1, publications);
        Assert.Equal(InstallationPhase.FailedNeedsAction, first.Store.Profile!.Phase);
        Assert.Equal("RuntimeEffectUnknown", first.Store.Profile.ErrorCode);
        Assert.True(first.Store.Account.Disabled);
        Assert.Null(first.Store.Account.PasswordHash);
        Assert.False(first.Installation.CommonOpen);
        Assert.Equal(503, await BrowserStatusAsync(first, "/api/admin/users"));
        Assert.Equal(replications, first.Native.Replications);
        Assert.Single(first.Accepted.Current!.Values);
        Assert.Single(first.Store.Issuers);
        Assert.Single(first.Store.Issues);
    }

    private static async Task<int> BrowserStatusAsync(ManagedBootstrapFixture first, string path)
    {
        RuntimeWriteGate gate = new();
        gate.Open(first.Installation.Current!.RuntimeGeneration);
        SetupBoundaryMiddleware boundary = new(context =>
        {
            context.Response.StatusCode = 204;
            return Task.CompletedTask;
        });
        DefaultHttpContext context = new();
        context.Request.Method = "GET";
        context.Request.Scheme = "https";
        context.Request.Host = new("browser.example.test");
        context.Request.Path = path;
        await boundary.InvokeAsync(context, Options.Create(first.Local), first.Installation, gate);
        return context.Response.StatusCode;
    }
}
