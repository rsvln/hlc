using Microsoft.AspNetCore.Mvc;
using HomeLabControl.Auth;
using HomeLabControl.Models;
using HomeLabControl.Services;
using Microsoft.AspNetCore.Authorization;

namespace HomeLabControl.Controllers
{
    /// <summary>
    /// REST-обёртка над PowerControlService (UI вызывает сервис напрямую).
    /// Работает только с хостами из PowerControl.yaml — произвольный ip:port не принимается.
    /// </summary>
    [ApiController]
    [Authorize(Policy = Policies.PowerView)]
    [Route("api/[controller]")]
    public class PowerControlController : ControllerBase
    {
        private readonly PowerControlService _powerService;
        private readonly UserService _users;
        private readonly AuditService _audit;

        public PowerControlController(PowerControlService powerService, UserService users, AuditService audit)
        {
            _powerService = powerService;
            _users = users;
            _audit = audit;
        }

        private HlcUser? CurrentUser => _users.Find(User.Identity?.Name);
        private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

        [HttpGet("settings")]
        public IActionResult GetSettings() => Ok(_powerService.GetSettings());

        [HttpGet("hosts")]
        public IActionResult GetHosts() => Ok(_powerService.GetHosts().Where(h => HlcAuth.HostAllowed(CurrentUser, h.Name)));

        [HttpPost("wol")]
        [Authorize(Policy = Policies.PowerControl)]
        public async Task<IActionResult> WakeOnLan([FromBody] WolRequest request)
        {
            var host = _powerService.GetHosts().FirstOrDefault(h => string.Equals(h.Mac, request.Mac, StringComparison.OrdinalIgnoreCase));
            if (host == null)
                return NotFound(new { error = "Host with this MAC not found in config" });
            if (!HlcAuth.HostAllowed(CurrentUser, host.Name))
                return Forbid();

            var ok = await _powerService.WakeOnLanAsync(host.Mac!);
            _audit.Log(User.Identity?.Name, "power.wol", host.Name, "api", ok, Ip);
            return ok ? Ok(new { message = "WOL packet sent" }) : StatusCode(500, new { error = "WOL failed" });
        }

        [HttpPost("shutdown")]
        [Authorize(Policy = Policies.PowerControl)]
        public async Task<IActionResult> Shutdown([FromBody] PowerRequest request)
        {
            var host = FindHost(request.Ip, request.Port);
            if (host == null)
                return NotFound(new { error = "Host not found in PowerControl config" });

            if (!HlcAuth.HostAllowed(CurrentUser, host.Name))
                return Forbid();

            var ok = await _powerService.ShutdownAsync(host.Ip, host.Port, request.Delay, request.WithDialog);
            _audit.Log(User.Identity?.Name, "power.shutdown", host.Name, "api", ok, Ip);
            return ok
                ? Ok(new { message = "Shutdown initiated" })
                : StatusCode(502, new { error = "Agent request failed" });
        }

        [HttpPost("reboot")]
        [Authorize(Policy = Policies.PowerControl)]
        public async Task<IActionResult> Reboot([FromBody] PowerRequest request)
        {
            var host = FindHost(request.Ip, request.Port);
            if (host == null)
                return NotFound(new { error = "Host not found in PowerControl config" });

            if (!HlcAuth.HostAllowed(CurrentUser, host.Name))
                return Forbid();

            var ok = await _powerService.RebootAsync(host.Ip, host.Port, request.Delay, request.WithDialog);
            _audit.Log(User.Identity?.Name, "power.reboot", host.Name, "api", ok, Ip);
            return ok
                ? Ok(new { message = "Reboot initiated" })
                : StatusCode(502, new { error = "Agent request failed" });
        }

        [HttpPost("cancel")]
        [Authorize(Policy = Policies.PowerControl)]
        public async Task<IActionResult> Cancel([FromBody] CancelRequest request)
        {
            var host = FindHost(request.Ip, request.Port);
            if (host == null)
                return NotFound(new { error = "Host not found in PowerControl config" });

            if (!HlcAuth.HostAllowed(CurrentUser, host.Name))
                return Forbid();

            var ok = await _powerService.CancelAsync(host.Ip, host.Port);
            _audit.Log(User.Identity?.Name, "power.cancel", host.Name, "api", ok, Ip);
            return ok
                ? Ok(new { message = "Cancelled" })
                : StatusCode(502, new { error = "Agent request failed" });
        }

        private Models.Host? FindHost(string ip, int port)
            => _powerService.GetHosts().FirstOrDefault(h => h.Ip == ip && h.Port == port);
    }

    public class WolRequest
    {
        public string Mac { get; set; } = string.Empty;
    }

    public class PowerRequest
    {
        public string Ip { get; set; } = string.Empty;
        public int Port { get; set; }
        public int Delay { get; set; } = 0;
        public bool WithDialog { get; set; } = false;
    }

    public class CancelRequest
    {
        public string Ip { get; set; } = string.Empty;
        public int Port { get; set; }
    }
}
