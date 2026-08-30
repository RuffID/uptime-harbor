using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using UptimeHarbor.Infrastructure.Identity;
using Microsoft.AspNetCore.RateLimiting;
using UptimeHarbor.Web.Security;

namespace UptimeHarbor.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting(WebSecurityPolicies.LOGIN_RATE_LIMIT_POLICY)]
public class LoginModel(IUserSignInService signInService) : PageModel
{
    [BindProperty]
    public string Login { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public bool RememberMe { get; set; }

    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Login) || string.IsNullOrWhiteSpace(Password))
        {
            ModelState.AddModelError(string.Empty, "Неверный логин или пароль.");
            return Page();
        }

        bool succeeded = await signInService.PasswordSignInAsync(
            Login,
            Password,
            RememberMe,
            cancellationToken);
        if (!succeeded)
        {
            ModelState.AddModelError(string.Empty, "Неверный логин или пароль.");
            return Page();
        }

        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }
}
