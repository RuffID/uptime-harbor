using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Identity;

public class AcceptedUserSignInService(RevisionStateStore state, AcceptedAccountSession sessions,
    ILocalAccountLogin localLogin, IHttpContextAccessor http, ILogger<AcceptedUserSignInService> logger)
    : IUserSignInService
{
    public async Task<bool> PasswordSignInAsync(string login, string password, bool isPersistent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!state.Available || string.IsNullOrWhiteSpace(login))
        {
            return Denied();
        }

        string normalized;
        try
        {
            normalized = SharedAccount.NormalizeLogin(login);
        }
        catch (ArgumentException)
        {
            return Denied();
        }

        AcceptedValue? value = state.Current?.Values.Values.SingleOrDefault(value => value.Kind == DeletedEntityKind.Account
            && SharedAccount.NormalizeLogin(value.Value.Name!) == normalized);
        if (value is null || !AcceptedAdministratorAccessPolicy.IsActiveAdministrator(state.Find(value.Id)))
        {
            return Denied();
        }

        SharedAccount account = RevisionAccounts.Materialize(value);
        ClaimsPrincipal principal = sessions.Create(account, IdentityConstants.ApplicationScheme);
        if (!await localLogin.CheckAsync(account, password, cancellationToken) || !sessions.IsValid(principal))
        {
            return Denied();
        }

        HttpContext context = http.HttpContext ?? throw new InvalidOperationException("LoginContextUnavailable");
        await context.SignInAsync(IdentityConstants.ApplicationScheme, principal,
            new AuthenticationProperties { IsPersistent = isPersistent });
        if (!sessions.IsValid(principal))
        {
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Denied();
        }

        logger.LogInformation("Accepted account sign-in succeeded for {UserId}.", account.UserId);
        return true;
    }

    private bool Denied()
    {
        logger.LogInformation("Accepted account sign-in denied.");
        return false;
    }

    public async Task SignOutAsync()
    {
        HttpContext context = http.HttpContext ?? throw new InvalidOperationException("LoginContextUnavailable");
        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        logger.LogInformation("Local account session signed out.");
    }
}
