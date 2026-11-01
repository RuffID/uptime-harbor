using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Identity;

/// <summary>Локальная привязка сессии к принятому аккаунту и текущему процессу узла.</summary>
public class AcceptedAccountSession
{
    public const string SESSION_CLAIM = "uptimeharbor:local-session";
    public const string CREDENTIAL_CLAIM = "uptimeharbor:credential-binding";
    private readonly string epoch = Guid.NewGuid().ToString("N");
    private readonly RevisionStateStore state;

    public AcceptedAccountSession(RevisionStateStore state)
    {
        this.state = state;
    }

    private string SessionEpoch()
    {
        return $"{epoch}/{state.RecoveryVersion}";
    }

    public ClaimsPrincipal Create(SharedAccount account, string authenticationType)
    {
        ClaimsIdentity identity = new(authenticationType);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, account.UserId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, account.Login));
        identity.AddClaim(new Claim(ClaimTypes.Role, ApplicationRoles.ADMINISTRATOR));
        identity.AddClaim(new Claim(SESSION_CLAIM, SessionEpoch()));
        identity.AddClaim(new Claim(CREDENTIAL_CLAIM, Binding(account.Credential)));
        return new ClaimsPrincipal(identity);
    }

    public bool IsValid(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true
            || principal.FindFirstValue(SESSION_CLAIM) != SessionEpoch()
            || !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out Guid userId))
        {
            return false;
        }

        if (!AcceptedAdministratorAccessPolicy.IsActiveAdministrator(state.Find(userId)))
        {
            return false;
        }

        AcceptedValue? value = state.Current?.Values.GetValueOrDefault(userId);
        if (value is null)
        {
            return false;
        }

        SharedAccount account = RevisionAccounts.Materialize(value);
        return principal.FindFirstValue(CREDENTIAL_CLAIM) == Binding(account.Credential);
    }

    private static string Binding(ProtectedCredential credential)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(credential.PasswordHash + "\n" + credential.SecurityStamp);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
