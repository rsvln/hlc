using HomeLabControl.Auth;
using HomeLabControl.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HomeLabControl.Pages;

/// <summary>Создание первого администратора. Доступна, только пока пользователей нет.</summary>
[AllowAnonymous]
public class SetupModel : PageModel
{
    private readonly UserService _users;

    public SetupModel(UserService users)
    {
        _users = users;
    }

    [BindProperty] public string UserName { get; set; } = "admin";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string Confirm { get; set; } = "";

    public string? Error { get; set; }

    public IActionResult OnGet() => _users.HasUsers ? Redirect("/login") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (_users.HasUsers)
            return Redirect("/login");

        try
        {
            if (string.IsNullOrWhiteSpace(UserName))
                throw new ArgumentException("Enter a user name");
            UserService.ValidatePassword(Password);
            if (Password != Confirm)
                throw new ArgumentException("Passwords do not match");
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
            return Page();
        }

        if (!await _users.CreateFirstAdminAsync(UserName, Password))
            return Redirect("/login");

        var user = _users.Find(UserName)!;
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            CookieValidation.CreatePrincipal(user),
            new AuthenticationProperties { IsPersistent = true });

        return Redirect("/");
    }
}
