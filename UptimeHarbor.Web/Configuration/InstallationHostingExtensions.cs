using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Web.Security;

namespace UptimeHarbor.Web.Configuration;

public static class InstallationHostingExtensions
{
    public static WebApplicationBuilder ConfigureLocalInstallation(this WebApplicationBuilder builder)
    {
        InstallationOptions options = builder.Configuration.GetSection(InstallationOptions.SECTION_NAME)
            .Get<InstallationOptions>() ?? new();
        if (options.InstallationIntent is not ("Legacy" or "Setup"))
        {
            throw new InvalidOperationException("InstallationIntentInvalid");
        }

        builder.Services.AddSingleton<DynamicPeerTls>();
        if (!options.Setup)
        {
            return builder;
        }

        ClusterOptions cluster = builder.Configuration.GetSection(ClusterOptions.SECTION_NAME).Get<ClusterOptions>() ?? new();
        if (cluster.Mode != "Disabled" || !Uri.TryCreate(options.BrowserOrigin, UriKind.Absolute, out Uri? origin)
            || origin.Scheme != "https" || origin.GetLeftPart(UriPartial.Authority) != options.BrowserOrigin)
        {
            throw new InvalidOperationException("ExplicitSetupWithBrowserHttpsOriginRequired");
        }

        InstallationLease lease = new(options, cluster.StoragePath);
        builder.Services.AddSingleton(lease);
        string keyring = Path.Combine(options.StateDirectory, "data-protection");
        ProtectedInstallationFiles.EnsureDirectory(keyring);
        builder.Services.AddDataProtection().SetApplicationName($"UptimeHarbor/{lease.InstallIdentity:N}")
            .PersistKeysToFileSystem(new DirectoryInfo(keyring));
        builder.Services.AddSetupAuthentication(lease.InstallIdentity);
        return builder;
    }

    public static IServiceCollection AddSetupAuthentication(this IServiceCollection services, Guid installIdentity)
    {
        services.AddAuthentication().AddCookie(SetupAuthentication.SCHEME, cookie =>
        {
            cookie.Cookie.Name = SetupAuthentication.COOKIE_NAME;
            cookie.Cookie.Path = "/";
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.ExpireTimeSpan = TimeSpan.FromMinutes(15);
            cookie.SlidingExpiration = true;
            cookie.LoginPath = "/Setup";
            cookie.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            cookie.Events.OnValidatePrincipal = async context =>
            {
                SetupSession? session = SetupAuthentication.Read(context.Principal);
                SetupAccountLogin login = context.HttpContext.RequestServices.GetRequiredService<SetupAccountLogin>();
                if (session is null || !await login.ValidateAsync(session, context.HttpContext.RequestAborted))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(SetupAuthentication.SCHEME);
                }
            };
        });
        services.AddOptions<CookieAuthenticationOptions>(SetupAuthentication.SCHEME)
            .Configure<IDataProtectionProvider>((cookie, provider) =>
                cookie.TicketDataFormat = new TicketDataFormat(provider.CreateProtector($"LocalSetup/{installIdentity:N}", "Cookie/v1")));
        return services;
    }
}
