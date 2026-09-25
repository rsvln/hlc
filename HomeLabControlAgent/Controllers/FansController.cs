// Controllers/FansController.cs
namespace HomeLabControlAgent.Controllers;

using HomeLabControlAgent.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class FansController : ControllerBase
{
    private readonly IHardwareMonitor _hardwareMonitor;
    private readonly ILogger<FansController> _logger;

    public FansController(IHardwareMonitor hardwareMonitor, ILogger<FansController> logger)
    {
        _hardwareMonitor = hardwareMonitor;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult GetFans()
    {
        try
        {
            _hardwareMonitor.Update();
            var fans = _hardwareMonitor.GetFans();
            return Ok(fans);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get fans");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{id}")]
    public IActionResult GetFan(string id)
    {
        try
        {
            _hardwareMonitor.Update();
            var fan = _hardwareMonitor.GetFan(id);
            
            if (fan == null)
                return NotFound(new { error = $"Fan {id} not found" });

            return Ok(fan);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get fan {FanId}", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPut("{id}/speed")]
    public IActionResult SetFanSpeed(string id, [FromBody] SetSpeedRequest request)
    {
        try
        {
            if (request.Speed < 0 || request.Speed > 100)
                return BadRequest(new { error = "Speed must be between 0 and 100" });

            var success = _hardwareMonitor.SetFanSpeed(id, request.Speed);
            
            if (!success)
                return NotFound(new { error = $"Fan {id} not found or not controllable" });

            _logger.LogInformation("Set fan {FanId} speed to {Speed}%", id, request.Speed);
            return Ok(new { fanId = id, speed = request.Speed });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set fan {FanId} speed", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPut("{id}/auto")]
    public IActionResult SetAutoMode(string id)
    {
        try
        {
            _hardwareMonitor.Update();
            var fan = _hardwareMonitor.GetFan(id);

            if (fan == null)
                return NotFound(new { error = "Fan not found" });

            if (!fan.SupportsControl)
                return BadRequest(new { error = "Fan does not support control" });

            if (!_hardwareMonitor.RestoreAutoMode(id))
                return StatusCode(500, new { error = "Failed to switch fan to automatic mode" });

            _logger.LogInformation("Fan {FanId} switched to automatic mode", id);
            return Ok(new { fanId = id, mode = "automatic" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set auto mode for fan {FanId}", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    public class SetSpeedRequest
    {
        public int Speed { get; set; }
    }
}