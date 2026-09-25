// Controllers/AgentController.cs
namespace HomeLabControlAgent.Controllers;

using System.Diagnostics;
using HomeLabControlAgent.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

[ApiController]
[Route("api/agent")]
public class AgentController : ControllerBase
{
    private readonly IOptionsMonitor<ApiKeyOptions> _authOptions;

    public AgentController(IOptionsMonitor<ApiKeyOptions> authOptions)
    {
        _authOptions = authOptions;
    }

    /// <summary>
    /// Версия и состояние агента. Без аутентификации — HomeLabControl опрашивает его для грида агентов.
    /// authRequired=false — ключи не настроены, API открыт; keyAccepted — принят ли переданный ключ.
    /// </summary>
    [HttpGet("info")]
    [AllowAnonymous]
    public IActionResult GetInfo()
    {
        var auth = _authOptions.CurrentValue;
        var authRequired = auth.Enabled && auth.ApiKeys.Values.Any(k => !string.IsNullOrWhiteSpace(k));
        var keyAccepted = authRequired && User.Identity?.IsAuthenticated == true;

        return Ok(new
        {
            version = VersionInfo.Version,
            buildDate = VersionInfo.BuildDate,
            os = OperatingSystem.IsWindows() ? "windows" : "linux",
            hostname = Environment.MachineName,
            uptimeSeconds = (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds,
            authRequired,
            keyAccepted,
            client = keyAccepted ? User.Identity?.Name : null
        });
    }
}
