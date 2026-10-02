// Controllers/MetricsController.cs
namespace HomeLabControlAgent.Controllers;

using System.Globalization;
using System.Text;
using HomeLabControlAgent.Models;
using HomeLabControlAgent.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Метрики в формате Prometheus: температуры, вентиляторы, SMART.
/// По умолчанию нужен API-ключ (в Prometheus: authorization: { credentials: "&lt;key&gt;" }),
/// Metrics:Anonymous=true — без ключа.
/// </summary>
[ApiController]
public class MetricsController : ControllerBase
{
    private readonly IHardwareMonitor _hardware;
    private readonly ISmartMonitorService _smart;
    private readonly SystemMonitor _system;
    private readonly IConfiguration _configuration;
    private readonly IAuthorizationService _authorization;

    public MetricsController(IHardwareMonitor hardware, ISmartMonitorService smart, SystemMonitor system,
        IConfiguration configuration, IAuthorizationService authorization)
    {
        _hardware = hardware;
        _smart = smart;
        _system = system;
        _configuration = configuration;
        _authorization = authorization;
    }

    [HttpGet("/metrics")]
    [AllowAnonymous]
    public async Task<IActionResult> Get()
    {
        // Анонимный доступ — только если явно разрешён; иначе та же проверка, что у остального API
        if (!_configuration.GetValue("Metrics:Anonymous", false) && User.Identity?.IsAuthenticated != true)
            return Challenge();

        var sb = new StringBuilder();

        Header(sb, "hlca_info", "Agent version and OS", "gauge");
        sb.Append($"hlca_info{{version=\"{Esc(VersionInfo.Version)}\",os=\"{(OperatingSystem.IsWindows() ? "windows" : "linux")}\"}} 1\n");

        // Ресурсы: CPU, память, диски, сеть
        var system = _system.Current;
        Header(sb, "hlca_cpu_percent", "CPU usage, percent", "gauge");
        if (system.CpuPercent.HasValue)
            sb.Append($"hlca_cpu_percent {Num(system.CpuPercent.Value)}\n");
        Header(sb, "hlca_memory_total_bytes", "Physical memory", "gauge");
        sb.Append($"hlca_memory_total_bytes {system.MemoryTotalBytes}\n");
        Header(sb, "hlca_memory_used_bytes", "Used physical memory", "gauge");
        sb.Append($"hlca_memory_used_bytes {system.MemoryUsedBytes}\n");
        Header(sb, "hlca_disk_total_bytes", "Disk size", "gauge");
        foreach (var d in system.Disks)
            sb.Append($"hlca_disk_total_bytes{{mount=\"{Esc(d.Mount)}\",device=\"{Esc(d.Device)}\"}} {d.TotalBytes}\n");
        Header(sb, "hlca_disk_free_bytes", "Free disk space available to users", "gauge");
        foreach (var d in system.Disks)
            sb.Append($"hlca_disk_free_bytes{{mount=\"{Esc(d.Mount)}\",device=\"{Esc(d.Device)}\"}} {d.FreeBytes}\n");
        Header(sb, "hlca_network_receive_bytes_per_second", "Network receive rate", "gauge");
        foreach (var n in system.Network)
            sb.Append($"hlca_network_receive_bytes_per_second{{iface=\"{Esc(n.Name)}\"}} {Num(n.RxBytesPerSecond)}\n");
        Header(sb, "hlca_network_transmit_bytes_per_second", "Network transmit rate", "gauge");
        foreach (var n in system.Network)
            sb.Append($"hlca_network_transmit_bytes_per_second{{iface=\"{Esc(n.Name)}\"}} {Num(n.TxBytesPerSecond)}\n");

        _hardware.Update();

        Header(sb, "hlca_temperature_celsius", "Temperature sensor value", "gauge");
        foreach (var s in _hardware.GetSensors().Where(s => s.Type == SensorType.Temperature))
            sb.Append($"hlca_temperature_celsius{{sensor=\"{Esc(s.Id)}\",name=\"{Esc(s.Name)}\"}} {Num(s.Value)}\n");

        var fans = _hardware.GetFans();
        Header(sb, "hlca_fan_rpm", "Fan speed, RPM", "gauge");
        foreach (var f in fans)
            sb.Append($"hlca_fan_rpm{{fan=\"{Esc(f.Id)}\",name=\"{Esc(f.Name)}\"}} {f.CurrentSpeed}\n");
        Header(sb, "hlca_fan_percent", "Fan PWM duty, percent", "gauge");
        foreach (var f in fans.Where(f => f.SupportsControl))
            sb.Append($"hlca_fan_percent{{fan=\"{Esc(f.Id)}\",name=\"{Esc(f.Name)}\"}} {f.CurrentSpeedPercent}\n");

        // SMART берётся из кэша (скан раз в SmartMonitor:ScanIntervalSeconds), не будит диски на каждый scrape
        var disks = await _smart.GetAllDisksAsync();
        Header(sb, "hlca_smart_healthy", "SMART overall health: 1 passed, 0 failed, -1 unknown", "gauge");
        foreach (var d in disks)
            sb.Append($"hlca_smart_healthy{Labels(d)} {(d.Health == SmartHealthStatus.Passed ? 1 : d.Health == SmartHealthStatus.Failed ? 0 : -1)}\n");
        Header(sb, "hlca_smart_temperature_celsius", "Disk temperature from SMART", "gauge");
        foreach (var d in disks.Where(d => d.Temperature.HasValue))
            sb.Append($"hlca_smart_temperature_celsius{Labels(d)} {Num(d.Temperature!.Value)}\n");
        Header(sb, "hlca_smart_power_on_hours", "Power-on hours", "counter");
        foreach (var d in disks.Where(d => d.PowerOnHours.HasValue))
            sb.Append($"hlca_smart_power_on_hours{Labels(d)} {d.PowerOnHours}\n");
        Header(sb, "hlca_smart_percent_used", "SSD wear, percent used", "gauge");
        foreach (var d in disks.Where(d => d.PercentUsed.HasValue))
            sb.Append($"hlca_smart_percent_used{Labels(d)} {d.PercentUsed}\n");
        Header(sb, "hlca_smart_attribute_warnings", "ATA attributes in warning or failed state", "gauge");
        foreach (var d in disks)
            sb.Append($"hlca_smart_attribute_warnings{Labels(d)} {d.Attributes.Count(a => a.Status != AttributeStatus.Ok)}\n");

        return Content(sb.ToString(), "text/plain; version=0.0.4; charset=utf-8");
    }

    private static void Header(StringBuilder sb, string name, string help, string type)
        => sb.Append($"# HELP {name} {help}\n# TYPE {name} {type}\n");

    private static string Labels(SmartDisk d)
        => $"{{disk=\"{Esc(d.Id)}\",model=\"{Esc(d.Model)}\",serial=\"{Esc(d.SerialNumber)}\"}}";

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // Экранирование значений меток по формату Prometheus
    private static string Esc(string? value)
        => (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
