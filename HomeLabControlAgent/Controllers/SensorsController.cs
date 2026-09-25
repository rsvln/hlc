// Controllers/SensorsController.cs
namespace HomeLabControlAgent.Controllers;

using Microsoft.AspNetCore.Mvc;
using HomeLabControlAgent.Services;

[ApiController]
[Route("api/[controller]")]
public class SensorsController : ControllerBase
{
    private readonly IHardwareMonitor _hardwareMonitor;
    private readonly ILogger<SensorsController> _logger;

    public SensorsController(IHardwareMonitor hardwareMonitor, ILogger<SensorsController> logger)
    {
        _hardwareMonitor = hardwareMonitor;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult GetSensors()
    {
        try
        {
            _hardwareMonitor.Update();
            var sensors = _hardwareMonitor.GetSensors();
            return Ok(sensors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get sensors");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{id}")]
    public IActionResult GetSensor(string id)
    {
        try
        {
            _hardwareMonitor.Update();
            var sensor = _hardwareMonitor.GetSensor(id);
            
            if (sensor == null)
                return NotFound(new { error = $"Sensor {id} not found" });

            return Ok(sensor);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get sensor {SensorId}", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }
}