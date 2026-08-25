using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using UptimeHarbor.Infrastructure.Identity;

namespace UptimeHarbor.Web.Pages.Account;

public class LogoutModel(IUserSignInService signInService) : PageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        await signInService.SignOutAsync();
        return RedirectToPage("/Account/Login");
    }
}
