using System.Text;

namespace UptimeHarbor.Domain.Cluster;

public class SharedAccount
{
    public const string ADMINISTRATOR = "Administrator";

    public SharedAccount(Guid userId, Guid roleId, string login, ProtectedCredential credential,
        bool isEnabled, long userRevision, DateTimeOffset? deletedAtUtc = null)
    {
        StorageGuards.Id(userId, nameof(userId));
        StorageGuards.Id(roleId, nameof(roleId));
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userRevision);
        string normalizedLogin = NormalizeLogin(login);
        if (deletedAtUtc.HasValue)
        {
            StorageGuards.Utc(deletedAtUtc.Value);
            if (isEnabled)
            {
                throw new ArgumentException("A deleted account cannot be enabled.");
            }
        }

        UserId = userId;
        RoleId = roleId;
        Login = login.Normalize(NormalizationForm.FormKC).Trim();
        NormalizedLogin = normalizedLogin;
        Credential = credential;
        IsEnabled = isEnabled;
        UserRevision = userRevision;
        DeletedAtUtc = deletedAtUtc;
    }

    public Guid UserId { get; }
    public Guid RoleId { get; }
    public string Login { get; }
    public string NormalizedLogin { get; }
    public ProtectedCredential Credential { get; }
    public bool IsEnabled { get; }
    public long UserRevision { get; }
    public DateTimeOffset? DeletedAtUtc { get; }

    public static string NormalizeLogin(string login)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        string value = login.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
        return StorageGuards.Text(value, 256, nameof(login));
    }

    // Упорядоченный reducer передаёт число активных администраторов принятого состояния.
    public void EnsureCanDeactivate(Guid actorUserId, int activeAdministratorCount)
    {
        StorageGuards.Id(actorUserId, nameof(actorUserId));
        ArgumentOutOfRangeException.ThrowIfNegative(activeAdministratorCount);
        if (actorUserId == UserId)
        {
            throw new InvalidOperationException("Self-disable and self-delete are forbidden.");
        }

        if (IsEnabled && activeAdministratorCount <= 1)
        {
            throw new InvalidOperationException("The last active administrator must remain enabled.");
        }
    }
}
