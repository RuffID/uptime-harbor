using System.Globalization;
using System.Security.Claims;
using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Web.Security;

public static class SetupAuthentication
{
    public const string SCHEME = "LocalSetup";
    public const string COOKIE_NAME = "__Host-UptimeHarbor.Setup";

    public static ClaimsPrincipal Principal(SetupSession session)
    {
        ClaimsIdentity identity = new(SCHEME);
        identity.AddClaim(new(ClaimTypes.NameIdentifier, session.AccountId.ToString("N")));
        identity.AddClaim(new("install", session.InstallIdentity.ToString("N")));
        identity.AddClaim(new("stamp", session.Stamp));
        identity.AddClaim(new("epoch", session.Epoch.ToString(CultureInfo.InvariantCulture)));
        identity.AddClaim(new("boot", session.BootId.ToString("N")));
        identity.AddClaim(new("started", session.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture)));
        return new(identity);
    }

    public static SetupSession? Read(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.AuthenticationType != SCHEME || principal.Identity.IsAuthenticated != true
            || !Guid.TryParseExact(principal.FindFirstValue(ClaimTypes.NameIdentifier), "N", out Guid account)
            || !Guid.TryParseExact(principal.FindFirstValue("install"), "N", out Guid install)
            || !Guid.TryParseExact(principal.FindFirstValue("boot"), "N", out Guid boot)
            || !long.TryParse(principal.FindFirstValue("epoch"), out long epoch)
            || !DateTimeOffset.TryParseExact(principal.FindFirstValue("started"), "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTimeOffset started))
        {
            return null;
        }

        string? stamp = principal.FindFirstValue("stamp");
        if (string.IsNullOrEmpty(stamp))
        {
            return null;
        }

        return new(account, install, stamp, epoch, boot, started);
    }
}
