using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class SetupPreparationTests
{
    [Fact]
    public async Task NewFirstHasSeparateCommonIdentityAndDurableDigestWithoutLeakingCredentials()
    {
        // Arrange
        using InstallationFixture fixture = new();
        await fixture.InitializeAsync();
        fixture.Cluster.ExpectedBotId = 123;
        SetupSession session = (await fixture.Login.LoginAsync("local-admin", InstallationFixture.ORIGINAL_PASSWORD, CancellationToken.None)).Data!;
        Credentials credentials = new();
        Log<InstallationPreparation> log = new();
        InstallationPreparation preparation = new(fixture.Store, fixture.State, fixture.Login, fixture.Preflight,
            fixture.Secrets, credentials, Options.Create(fixture.Options), fixture.Time, log, fixture.BotIdentity);
        SetupPreparationRequest request = new("Bootstrap", 1, 1, "common-admin", "Common-Z9!secret");

        // Act
        ServiceResult<SetupStatus> first = await preparation.PrepareAsync(session, request, CancellationToken.None);
        LocalInstallationProfile saved = fixture.Store.Profile!;
        ServiceResult<SetupStatus> retry = await preparation.PrepareAsync(session, request, CancellationToken.None);
        ServiceResult<SetupStatus> changed = await preparation.PrepareAsync(session, request with { CommonPassword = "Changed-Z9!secret" }, CancellationToken.None);
        byte[] bytes = fixture.Secrets.Read(saved.AttemptId!.Value, "bootstrap", saved.BootstrapAccountReference!);
        RevisionCommand account = RevisionCodec.Decode(bytes);

        // Assert
        Assert.True(first.Success);
        Assert.True(retry.Success);
        Assert.False(changed.Success);
        Assert.Equal(saved, fixture.Store.Profile);
        Assert.Equal(123, saved.BotId);
        Assert.NotEqual(fixture.Store.Account!.Id, account.EntityId);
        Assert.NotEqual(fixture.Store.Account.PasswordHash, account.Value!.PasswordHash);
        Assert.Equal(saved.BootstrapAccountDigest, RevisionCodec.Digest(bytes));
        Assert.Equal(saved.OperationIds!["bootstrap"], account.OperationId);
        Assert.Equal("BootstrapAccount", account.Action);
        Assert.Single(log.Messages);
        Assert.DoesNotContain("Common-Z9!secret", log.Messages.Single());
        Assert.DoesNotContain(account.Value.PasswordHash!, log.Messages.Single());
        Assert.DoesNotContain("common-admin", log.Messages.Single());
        Assert.False(fixture.State.CommonOpen);
    }

    [Fact]
    public async Task MissingConfiguredBotIdAndFailedCommitCannotReportPreparationSuccess()
    {
        // Arrange
        using InstallationFixture fixture = new();
        await fixture.InitializeAsync();
        SetupSession session = (await fixture.Login.LoginAsync("local-admin", InstallationFixture.ORIGINAL_PASSWORD, CancellationToken.None)).Data!;
        Log<InstallationPreparation> log = new();
        InstallationPreparation preparation = new(fixture.Store, fixture.State, fixture.Login, fixture.Preflight,
            fixture.Secrets, new Credentials(), Options.Create(fixture.Options), fixture.Time, log, fixture.BotIdentity);
        SetupPreparationRequest request = new("Bootstrap", 1, 1, "common-admin", "Common-Z9!secret");

        // Act
        ServiceResult<SetupStatus> missingBot = await preparation.PrepareAsync(session, request, CancellationToken.None);
        fixture.Cluster.ExpectedBotId = 123;
        fixture.Store.BeforeCommit = () => throw new IOException("CommitFailed");
        Exception? commit = await Record.ExceptionAsync(() => preparation.PrepareAsync(session, request, CancellationToken.None));

        // Assert
        Assert.False(missingBot.Success);
        Assert.IsType<IOException>(commit);
        Assert.Empty(log.Messages);
        Assert.Equal(InstallationPhase.SetupReady, fixture.Store.Profile!.Phase);
        Assert.Null(fixture.State.Current!.AttemptId);
        Assert.False(fixture.State.CommonOpen);
        Assert.Throws<InvalidDataException>(() => fixture.Secrets.ReconcileAll(fixture.Store.Profile));
    }

    private class Credentials : IAccountCredentialService
    {
        private readonly PasswordHasher<ApplicationUser> hasher = new();
        public Task<ServiceResult<ProtectedCredential>> PrepareAsync(Guid id, string login, string password, CancellationToken token)
        {
            ProtectedCredential result = new(hasher.HashPassword(new(id, login), password), 1, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            return Task.FromResult(ServiceResult<ProtectedCredential>.Ok(result));
        }

        public bool Verify(Guid id, string login, ProtectedCredential credential, string password)
        {
            return hasher.VerifyHashedPassword(new(id, login), credential.PasswordHash, password) != PasswordVerificationResult.Failed;
        }
    }

    private class Log<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel level)
        {
            return true;
        }
        void ILogger.Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
