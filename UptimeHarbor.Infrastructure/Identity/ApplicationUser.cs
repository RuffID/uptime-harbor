using EFCoreLibrary.Abstractions.Entity;
using Microsoft.AspNetCore.Identity;
using UptimeHarbor.Domain.Cluster;
using System.Text;

namespace UptimeHarbor.Infrastructure.Identity;

public class ApplicationUser : IdentityUser<Guid>, IEntity<Guid>
{
    private ApplicationUser()
    {
    }

    public ApplicationUser(Guid id, string login)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор пользователя не может быть пустым.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(login);

        Id = id;
        UserName = login.Normalize(NormalizationForm.FormKC).Trim();
        NormalizedUserName = SharedAccount.NormalizeLogin(login);
        IsEnabled = true;
    }

    public bool IsEnabled { get; private set; }

    public long UserRevision { get; private set; }

    public int PasswordHashFormatVersion { get; private set; } = 1;

    public DateTimeOffset? DeletedAtUtc { get; private set; }

    public void ApplySharedAccount(SharedAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.UserId != Id || account.UserRevision <= UserRevision)
        {
            throw new InvalidOperationException("Shared account identity or revision is stale.");
        }

        UserName = account.Login;
        NormalizedUserName = account.NormalizedLogin;
        PasswordHash = account.Credential.PasswordHash;
        PasswordHashFormatVersion = account.Credential.FormatVersion;
        SecurityStamp = account.Credential.SecurityStamp;
        ConcurrencyStamp = account.Credential.ConcurrencyStamp;
        IsEnabled = account.IsEnabled;
        UserRevision = account.UserRevision;
        DeletedAtUtc = account.DeletedAtUtc;
        // AccessFailedCount, LockoutEnd и локальные сессии сохраняются.
    }

    public void Enable()
    {
        IsEnabled = true;
    }

    public void Disable()
    {
        IsEnabled = false;
    }
}
