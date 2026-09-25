using HomeLabControl.Auth;
using HomeLabControl.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HomeLabControl.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly UserService _users;
    private readonly LoginThrottle _throttle;
    private readonly ILogger<LoginModel> _logger;
    private readonly AuditService _audit;

    public LoginModel(UserService users, LoginThrottle throttle, ILogger<LoginModel> logger, AuditService audit)
    {
        _users = users;
        _throttle = throttle;
        _logger = logger;
        _audit = audit;
    }

    [BindProperty] public string UserName { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public bool RememberMe { get; set; } = true;
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public string? Error { get; set; }

    public IActionResult OnGet()
    {
        if (!_users.HasUsers)
            return Redirect("/setup");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!_users.HasUsers)
            return Redirect("/setup");

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?";
        var delay = _throttle.GetDelay(ip);
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay);

        var user = _users.Validate(UserName, Password);
        if (user == null)
        {
            _throttle.Fail(ip);
            _logger.LogWarning("Failed login for '{User}' from {Ip}", UserName, ip);
            _audit.Log(UserName, "login", success: false, ip: ip);
            Error = "Invalid user name or password";
            Password = "";
            return Page();
        }

        _throttle.Success(ip);
        _logger.LogInformation("User '{User}' signed in from {Ip}", user.Name, ip);
        _audit.Log(user.Name, "login", ip: ip);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            CookieValidation.CreatePrincipal(user),
            new AuthenticationProperties { IsPersistent = RememberMe });

        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }
}
