using System.Collections.Concurrent;
using System.Security.Claims;
using HomeLabControl.Models;
using HomeLabControl.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HomeLabControl.Auth;

/// <summary>Имена политик: "admin" и "&lt;module&gt;:&lt;view|control&gt;".</summary>
public static class Policies
{
    public const string Admin = "admin";

    public const string PowerView = "power:view";
    public const string PowerControl = "power:control";
    public const string FanView = "fanControl:view";
    public const string FanControl = "fanControl:control";
    public const string SmartView = "smart:view";
    public const string BackupView = "backup:view";
    public const string BackupControl = "backup:control";

    public const string StampClaim = "hlc:stamp";

    public static void Register(AuthorizationOptions options)
    {
        options.AddPolicy(Admin, p => p.AddRequirements(new HlcPermissionRequirement(null, null)));

        foreach (var module in HlcModules.All)
        {
            options.AddPolicy($"{module}:{HlcModules.View}", p => p.AddRequirements(new HlcPermissionRequirement(module, HlcModules.View)));
            if (HlcModules.HasControl(module))
                options.AddPolicy($"{module}:{HlcModules.Control}", p => p.AddRequirements(new HlcPermissionRequirement(module, HlcModules.Control)));
        }

        // Всё закрыто, кроме [AllowAnonymous] (login / setup)
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    }
}

/// <summary>module == null — требуется администратор.</summary>
public record HlcPermissionRequirement(string? Module, string? Level) : IAuthorizationRequirement;

/// <summary>
/// Проверяет права по актуальной записи в users.yaml, а не по claims из cookie:
/// изменение прав действует сразу, без повторного входа (в Blazor — после обновления страницы или на следующем действии).
/// </summary>
public class HlcPermissionHandler : AuthorizationHandler<HlcPermissionRequirement>
{
    private readonly UserService _users;

    public HlcPermissionHandler(UserService users)
    {
        _users = users;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, HlcPermissionRequirement requirement)
    {
        var user = _users.Find(context.User.Identity?.Name);
        if (user == null || user.Disabled)
            return Task.CompletedTask;

        if (HasPermission(user, requirement.Module, requirement.Level))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }

    public static bool HasPermission(HlcUser user, string? module, string? level)
    {
        if (user.Admin)
            return true;
        if (module == null)
            return false;
        if (!user.Permissions.TryGetValue(module, out var granted))
            return false;

        // control включает view
        return level == HlcModules.View || granted == HlcModules.Control;
    }
}

/// <summary>
/// Проверка прав из Blazor-компонентов: и для скрытия кнопок, и как серверная защита обработчиков
/// (скрытая кнопка — не защита; в обработчике всегда вызывать <see cref="RequireAsync"/>).
/// </summary>
public class HlcAuth
{
    private readonly AuthenticationStateProvider _stateProvider;
    private readonly IAuthorizationService _authorization;

    public HlcAuth(AuthenticationStateProvider stateProvider, IAuthorizationService authorization)
    {
        _stateProvider = stateProvider;
        _authorization = authorization;
    }

    public async Task<bool> CanAsync(string policy)
    {
        var state = await _stateProvider.GetAuthenticationStateAsync();
        return (await _authorization.AuthorizeAsync(state.User, policy)).Succeeded;
    }

    public async Task RequireAsync(string policy)
    {
        if (!await CanAsync(policy))
            throw new UnauthorizedAccessException($"Access denied: {policy}");
    }

    public async Task<string?> GetUserNameAsync()
        => (await _stateProvider.GetAuthenticationStateAsync()).User.Identity?.Name;
}

/// <summary>Сбрасывает cookie удалённого/отключённого пользователя и после смены пароля/прав.</summary>
public static class CookieValidation
{
    public static Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var users = context.HttpContext.RequestServices.GetRequiredService<UserService>();
        var user = users.Find(context.Principal?.Identity?.Name);
        var stamp = context.Principal?.FindFirst(Policies.StampClaim)?.Value;

        if (user == null || user.Disabled || user.SecurityStamp != stamp)
            context.RejectPrincipal();

        return Task.CompletedTask;
    }

    public static ClaimsPrincipal CreatePrincipal(HlcUser user)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(Policies.StampClaim, user.SecurityStamp)
        }, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }
}

/// <summary>Защита от перебора: после 5 неудач с одного IP за 10 минут — пауза, растущая до минуты.</summary>
public class LoginThrottle
{
    private readonly ConcurrentDictionary<string, (int Failures, DateTime First)> _failures = new();

    public TimeSpan GetDelay(string ip)
    {
        if (!_failures.TryGetValue(ip, out var entry) || DateTime.UtcNow - entry.First > TimeSpan.FromMinutes(10))
            return TimeSpan.Zero;

        return entry.Failures < 5 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(60, 5 * (entry.Failures - 4)));
    }

    public void Fail(string ip)
        => _failures.AddOrUpdate(ip,
            _ => (1, DateTime.UtcNow),
            (_, e) => DateTime.UtcNow - e.First > TimeSpan.FromMinutes(10) ? (1, DateTime.UtcNow) : (e.Failures + 1, e.First));

    public void Success(string ip) => _failures.TryRemove(ip, out _);
}
