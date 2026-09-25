// Controllers/PowerController.cs
namespace HomeLabControlAgent.Controllers;

using HomeLabControlAgent.Models;
using HomeLabControlAgent.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class PowerController : ControllerBase
{
    private const int MaxDelaySeconds = 24 * 60 * 60;

    private readonly IPowerService _powerService;
    private readonly ILogger<PowerController> _logger;

    public PowerController(IPowerService powerService, ILogger<PowerController> logger)
    {
        _powerService = powerService;
        _logger = logger;
    }

    /// <summary>
    /// Выключить компьютер
    /// </summary>
    /// <param name="delay">Задержка в секундах (0 = немедленно). Отложенное выключение отменяется через /cancel</param>
    [HttpPost("shutdown")]
    public async Task<ActionResult<PowerResponse>> Shutdown([FromQuery] int delay = 0)
    {
        if (!IsValidDelay(delay))
            return BadRequest(InvalidDelay());

        _logger.LogInformation("Shutdown requested by {Client} with delay: {Delay}s", User.Identity?.Name, delay);
        var result = await _powerService.ShutdownAsync(delay);

        return result.Success
            ? Ok(result)
            : StatusCode(500, result);
    }

    /// <summary>
    /// Перезагрузить компьютер
    /// </summary>
    /// <param name="delay">Задержка в секундах (0 = немедленно). Отложенная перезагрузка отменяется через /cancel</param>
    [HttpPost("reboot")]
    public async Task<ActionResult<PowerResponse>> Reboot([FromQuery] int delay = 0)
    {
        if (!IsValidDelay(delay))
            return BadRequest(InvalidDelay());

        _logger.LogInformation("Reboot requested by {Client} with delay: {Delay}s", User.Identity?.Name, delay);
        var result = await _powerService.RebootAsync(delay);

        return result.Success
            ? Ok(result)
            : StatusCode(500, result);
    }

    /// <summary>
    /// Выключить компьютер с диалоговым окном (только Windows)
    /// </summary>
    /// <param name="delay">Задержка в секундах</param>
    /// <param name="message">Сообщение в диалоговом окне</param>
    [HttpPost("shutdown-with-dialog")]
    public async Task<ActionResult<PowerResponse>> ShutdownWithDialog(
        [FromQuery] int delay = 30,
        [FromQuery] string message = "Компьютер будет выключен")
    {
        if (!IsValidDelay(delay))
            return BadRequest(InvalidDelay());

        _logger.LogInformation("Shutdown with dialog requested by {Client}: delay={Delay}s", User.Identity?.Name, delay);
        var result = await _powerService.ShutdownWithDialogAsync(delay, message);

        return result.Success
            ? Ok(result)
            : StatusCode(400, result);
    }

    /// <summary>
    /// Перезагрузить компьютер с диалоговым окном (только Windows)
    /// </summary>
    /// <param name="delay">Задержка в секундах</param>
    /// <param name="message">Сообщение в диалоговом окне</param>
    [HttpPost("reboot-with-dialog")]
    public async Task<ActionResult<PowerResponse>> RebootWithDialog(
        [FromQuery] int delay = 30,
        [FromQuery] string message = "Компьютер будет перезагружен")
    {
        if (!IsValidDelay(delay))
            return BadRequest(InvalidDelay());

        _logger.LogInformation("Reboot with dialog requested by {Client}: delay={Delay}s", User.Identity?.Name, delay);
        var result = await _powerService.RebootWithDialogAsync(delay, message);

        return result.Success
            ? Ok(result)
            : StatusCode(400, result);
    }

    /// <summary>
    /// Отменить запланированное выключение/перезагрузку (включая открытый диалог на Windows)
    /// </summary>
    [HttpPost("cancel")]
    [HttpPost("cancel-shutdown")] // алиас — старые клиенты HomeLabControl вызывали этот путь
    public async Task<ActionResult<PowerResponse>> CancelShutdown()
    {
        _logger.LogInformation("Cancel shutdown requested by {Client}", User.Identity?.Name);
        var result = await _powerService.CancelShutdownAsync();

        return result.Success
            ? Ok(result)
            : StatusCode(500, result);
    }

    /// <summary>
    /// Проверка доступности API (без аутентификации — годится для health-check в HA/мониторинге)
    /// </summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public IActionResult GetStatus()
    {
        // Детали о запланированном действии отдаём только аутентифицированным
        var pending = User.Identity?.IsAuthenticated == true ? _powerService.GetPending() : null;

        return Ok(new
        {
            status = "ok",
            platform = OperatingSystem.IsWindows() ? "Windows" : "Linux",
            dialogSupport = OperatingSystem.IsWindows(),
            pending
        });
    }

    private static bool IsValidDelay(int delay) => delay is >= 0 and <= MaxDelaySeconds;

    private static PowerResponse InvalidDelay() => new()
    {
        Success = false,
        Error = $"delay must be between 0 and {MaxDelaySeconds} seconds"
    };
}
