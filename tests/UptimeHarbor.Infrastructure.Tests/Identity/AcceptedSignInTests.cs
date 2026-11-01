using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Identity;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Identity;

public class AcceptedSignInTests
{
    [Fact]
    public async Task ApplyWhileIssuingCookieRejectsLoginAndRemovesStaleTicketWithoutSuccessLog()
    {
        // Arrange
        using AcceptedIdentityFixture fixture = new();
        SharedAccount account = await fixture.CreateAsync();
        fixture.Authentication.BeforeSignIn = () => fixture.Publish(new SharedAccount(account.UserId,
            account.RoleId, account.Login, account.Credential, false, 2));

        // Act
        bool result = await fixture.SignIn.PasswordSignInAsync("admin", "Secret-123!", false, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result);
        Assert.Null(fixture.Authentication.Principal);
        Assert.DoesNotContain(fixture.Logs.Messages, message => message.Contains("succeeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SameAcceptedAccountSignsInOnTwoNodesWithLocalSessionsAndLockout()
    {
        // Arrange
        using AcceptedIdentityFixture first = new();
        using AcceptedIdentityFixture second = new();
        SharedAccount account = await first.CreateAsync();
        second.Publish(account);

        // Act
        foreach (int attempt in Enumerable.Range(0, 5))
        {
            Assert.False(await first.SignIn.PasswordSignInAsync("admin", "wrong", false, TestContext.Current.CancellationToken));
        }

        bool locked = await first.SignIn.PasswordSignInAsync("admin", "Secret-123!", false, TestContext.Current.CancellationToken);
        bool otherNode = await second.SignIn.PasswordSignInAsync("ＡＤＭＩＮ", "Secret-123!", false, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(locked);
        Assert.True(otherNode);
        Assert.Equal(0, second.Work.User!.AccessFailedCount);
        Assert.Null(second.Work.User.LockoutEnd);
        Assert.True(second.Sessions.IsValid(second.Authentication.Principal));
        Assert.False(first.Sessions.IsValid(second.Authentication.Principal));
        Assert.Equal(account.UserId.ToString(), second.Authentication.Principal!.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        Assert.Empty(first.State.Current!.Results);
        Assert.Equal(account.Credential.ConcurrencyStamp, first.Work.User!.ConcurrencyStamp);
        Assert.Equal(account.Credential.PasswordHash, first.Work.User.PasswordHash);

        // Act
        first.Time.Now = first.Time.Now.AddMinutes(6);
        bool afterTimeout = await first.SignIn.PasswordSignInAsync("admin", "Secret-123!", true, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(afterTimeout);
        Assert.Null(first.Work.User.LockoutEnd);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("unaccepted")]
    [InlineData("recovery")]
    public async Task UnacceptedOrInactiveOrUntrustedStateCannotSignIn(string condition)
    {
        // Arrange
        using AcceptedIdentityFixture fixture = new();
        SharedAccount account = await fixture.CreateAsync();
        if (condition is "disabled" or "deleted")
        {
            DateTimeOffset? deleted = null;
            if (condition == "deleted")
            {
                deleted = DateTimeOffset.UnixEpoch;
            }

            fixture.Publish(new SharedAccount(account.UserId, account.RoleId, account.Login, account.Credential, false, 2, deleted));
        }
        else if (condition == "unaccepted")
        {
            fixture.State.Publish(new RevisionImage(fixture.State.Current!.ClusterId, 1));
        }
        else
        {
            fixture.State.BeginRecovery();
        }

        // Act
        bool result = await fixture.SignIn.PasswordSignInAsync("admin", "Secret-123!", false, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result);
        Assert.Null(fixture.Authentication.Principal);
        Assert.Equal(0, fixture.Work.Saves);
        Assert.DoesNotContain(account.Credential.PasswordHash, string.Join(" ", fixture.Logs.Messages));
        Assert.DoesNotContain(account.Credential.SecurityStamp, string.Join(" ", fixture.Logs.Messages));
        Assert.DoesNotContain("Secret-123!", string.Join(" ", fixture.Logs.Messages));
    }

    [Fact]
    public async Task OfflineAcceptedVersionRemainsUsableUntilCatchUpAndOldCookieNeverRevives()
    {
        // Arrange
        using AcceptedIdentityFixture fixture = new();
        SharedAccount account = await fixture.CreateAsync();
        await fixture.SignIn.PasswordSignInAsync("admin", "Secret-123!", false, TestContext.Current.CancellationToken);
        ClaimsPrincipal oldSession = fixture.Authentication.Principal!;

        // Act
        bool offline = await fixture.SignIn.PasswordSignInAsync("admin", "Secret-123!", false, TestContext.Current.CancellationToken);
        var nextCredential = await fixture.Credentials.PrepareAsync(account.UserId, "admin", "NewSecret-123!", TestContext.Current.CancellationToken);
        fixture.Publish(new SharedAccount(account.UserId, account.RoleId, "admin", nextCredential.Data!, true, 2));
        bool oldPassword = await fixture.SignIn.PasswordSignInAsync("admin", "Secret-123!", false, TestContext.Current.CancellationToken);
        bool newPassword = await fixture.SignIn.PasswordSignInAsync("admin", "NewSecret-123!", false, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(offline);
        Assert.False(fixture.Sessions.IsValid(oldSession));
        Assert.False(oldPassword);
        Assert.True(newPassword);
        ClaimsPrincipal beforeRestore = fixture.Authentication.Principal!;

        // Act
        fixture.State.BeginRecovery();
        bool whileRecovering = fixture.Sessions.IsValid(beforeRestore);
        fixture.State.ConfirmRecovery(fixture.State.Current!, fixture.State.RecoveryVersion);

        // Assert
        Assert.False(whileRecovering);
        Assert.False(fixture.Sessions.IsValid(beforeRestore));
        Assert.True(await fixture.SignIn.PasswordSignInAsync("admin", "NewSecret-123!", false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplyDuringPasswordCheckCannotIssueStaleSessionOrFalseSuccessLog()
    {
        // Arrange
        using AcceptedIdentityFixture fixture = new();
        SharedAccount account = await fixture.CreateAsync();
        fixture.Work.BeforeSave = () =>
        {
            fixture.Publish(new SharedAccount(account.UserId, account.RoleId, "admin", account.Credential, false, 2));
            return Task.CompletedTask;
        };

        // Act
        bool result = await fixture.SignIn.PasswordSignInAsync("admin", "Secret-123!", false, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result);
        Assert.Null(fixture.Authentication.Principal);
        Assert.DoesNotContain(fixture.Logs.Messages, message => message.Contains("succeeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoginDoesNotRehashSharedCredentialAndLocalApplyPreservesCounters()
    {
        // Arrange
        using AcceptedIdentityFixture fixture = new();
        SharedAccount account = await fixture.CreateAsync();
        fixture.Work.User!.AccessFailedCount = 3;

        // Act
        fixture.Publish(new SharedAccount(account.UserId, account.RoleId, "admin", account.Credential, true, 2));
        int failuresAfterApply = fixture.Work.User.AccessFailedCount;
        await fixture.SignIn.PasswordSignInAsync("admin", "Secret-123!", false, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, failuresAfterApply);
        Assert.Equal(0, fixture.Work.User.AccessFailedCount);
        Assert.Equal(account.Credential.PasswordHash, fixture.Work.User.PasswordHash);
        Assert.Equal(account.Credential.SecurityStamp, fixture.Work.User.SecurityStamp);
        Assert.Equal(account.Credential.ConcurrencyStamp, fixture.Work.User.ConcurrencyStamp);
        Assert.Empty(fixture.State.Current!.Results);
        Assert.DoesNotContain(fixture.Authentication.Principal!.Claims, claim => claim.Value == account.Credential.PasswordHash);
    }
}
