// Controllers/SmartController.cs
namespace HomeLabControlAgent.Controllers;

using HomeLabControlAgent.Models;
using HomeLabControlAgent.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/smart")]
public class SmartController : ControllerBase
{
    private readonly ISmartMonitorService _smartService;
    private readonly ILogger<SmartController> _logger;

    public SmartController(ISmartMonitorService smartService, ILogger<SmartController> logger)
    {
        _smartService = smartService;
        _logger = logger;
    }

    /// <summary>Получить список всех дисков с SMART данными</summary>
    [HttpGet("disks")]
    public async Task<ActionResult<List<SmartDisk>>> GetDisks()
    {
        var disks = await _smartService.GetAllDisksAsync();
        return Ok(disks);
    }

    /// <summary>Получить данные конкретного диска</summary>
    [HttpGet("disks/{diskId}")]
    public async Task<ActionResult<SmartDisk>> GetDisk(string diskId)
    {
        var disk = await _smartService.GetDiskAsync(diskId);
        if (disk == null)
            return NotFound($"Disk '{diskId}' not found");

        return Ok(disk);
    }

    /// <summary>Принудительно пересканировать диски</summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        _logger.LogInformation("Manual SMART refresh requested");
        await _smartService.RefreshAsync();
        return Ok();
    }
}
