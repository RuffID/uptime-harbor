using Microsoft.AspNetCore.Identity;

namespace UptimeHarbor.Infrastructure.Identity;

public class UserSignInService(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager)
    : IUserSignInService
{
    public async Task<bool> PasswordSignInAsync(
        string login,
        string password,
        bool isPersistent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplicationUser? user = await userManager.FindByNameAsync(login);
        if (user is null || !user.IsEnabled)
        {
            return false;
        }

        SignInResult result = await signInManager.PasswordSignInAsync(
            user,
            password,
            isPersistent,
            lockoutOnFailure: true);
        return result.Succeeded;
    }

    public Task SignOutAsync() => signInManager.SignOutAsync();
}
