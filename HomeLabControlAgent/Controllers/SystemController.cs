// Controllers/SystemController.cs
namespace HomeLabControlAgent.Controllers;

using HomeLabControlAgent.Models;
using HomeLabControlAgent.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class SystemController : ControllerBase
{
    private readonly SystemMonitor _monitor;

    public SystemController(SystemMonitor monitor)
    {
        _monitor = monitor;
    }

    /// <summary>CPU, память, место на дисках и скорость сети (замер раз в 5 с).</summary>
    [HttpGet]
    public ActionResult<SystemInfo> Get() => Ok(_monitor.Current);
}
