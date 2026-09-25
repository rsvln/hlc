using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HomeLabControl.Pages;

[AllowAnonymous]
// Cookie с SameSite=Lax не уходит в кросс-сайтовом POST, поэтому antiforgery здесь не нужен
[IgnoreAntiforgeryToken]
public class LogoutModel : PageModel
{
    // Только POST, чтобы выход нельзя было вызвать ссылкой
    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/login");
    }
}
