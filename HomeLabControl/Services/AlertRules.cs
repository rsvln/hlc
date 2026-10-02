using HomeLabControl.Models;

namespace HomeLabControl.Services;

/// <summary>Выбор порога для датчика / диска: переопределение хоста → класс у хоста → глобальный класс → по умолчанию.</summary>
public static class AlertRules
{
    /// <summary>Гистерезис: тревога снимается, только когда значение вернулось за порог с запасом.</summary>
    public const double TemperatureHysteresis = 3;
    public const double DiskHysteresisPercent = 2;

    private static readonly string[] LinuxCpu = { "coretemp", "k10temp", "zenpower", "cpu_thermal", "cpu" };
    private static readonly string[] LinuxGpu = { "amdgpu", "nouveau", "radeon", "nvidia" };
    private static readonly string[] LinuxStorage = { "nvme", "drivetemp", "disk" };

    /// <summary>
    /// Класс железа по id датчика агента: Windows — "{HardwareType}__…" (Cpu, GpuNvidia, Storage…),
    /// Linux — "{hwmon}_temp…" (coretemp, nvme, drivetemp…), "nvidia_gpu0_temp0", "disk_sda_temp".
    /// </summary>
    public static string? SensorClass(string sensorId)
    {
        var sep = sensorId.IndexOf("__", StringComparison.Ordinal);
        if (sep > 0)
        {
            var type = sensorId[..sep];
            if (type.StartsWith("Cpu", StringComparison.OrdinalIgnoreCase)) return "cpu";
            if (type.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase)) return "gpu";
            if (type.StartsWith("Storage", StringComparison.OrdinalIgnoreCase)) return "storage";
            return null;
        }

        var device = sensorId.Split('_')[0].ToLowerInvariant();
        if (LinuxCpu.Contains(device)) return "cpu";
        if (LinuxGpu.Contains(device)) return "gpu";
        if (LinuxStorage.Contains(device)) return "storage";
        return null;
    }

    /// <summary>"ITE IT8613E - Temperature #2" → "Temperature #2".</summary>
    public static string ShortName(string name)
    {
        var i = name.LastIndexOf(" - ", StringComparison.Ordinal);
        return i >= 0 ? name[(i + 3)..] : name;
    }

    private static bool Matches(IEnumerable<string>? list, SensorInfo sensor)
        => list?.Any(x => x.Equals(sensor.Id, StringComparison.OrdinalIgnoreCase) ||
                          x.Equals(sensor.Name, StringComparison.OrdinalIgnoreCase) ||
                          x.Equals(ShortName(sensor.Name), StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// Порог температуры датчика; null — не проверять. Датчики, скрытые в fanControl.ignoredSensors,
    /// тоже не проверяются: обычно это «мусор» вроде AUXTIN с -11°.
    /// </summary>
    public static double? TemperatureLimit(SensorInfo sensor, AlertSettings global, HostAlerts? host, IEnumerable<string>? fanIgnored = null)
    {
        if (Matches(host?.IgnoreSensors, sensor) || Matches(fanIgnored, sensor))
            return null;

        double? limit = null;
        var perHost = host?.Temperature;
        if (perHost != null)
        {
            // Конкретный датчик важнее класса
            var key = perHost.Keys.FirstOrDefault(k =>
                k.Equals(sensor.Id, StringComparison.OrdinalIgnoreCase) ||
                k.Equals(sensor.Name, StringComparison.OrdinalIgnoreCase) ||
                k.Equals(ShortName(sensor.Name), StringComparison.OrdinalIgnoreCase));
            if (key != null)
                limit = perHost[key];
        }

        var cls = SensorClass(sensor.Id);
        if (limit == null && cls != null)
        {
            if (perHost != null && perHost.TryGetValue(cls, out var hostClass))
                limit = hostClass;
            else if (global.Temperature != null && global.Temperature.TryGetValue(cls, out var globalClass))
                limit = globalClass;
            else if (AlertSettings.DefaultTemperature.TryGetValue(cls, out var defaultClass))
                limit = defaultClass;
        }

        return limit is > 0 ? limit : null;
    }

    /// <summary>Порог свободного места, %; null — не проверять.</summary>
    public static double? DiskFreeLimit(DiskSpaceInfo disk, AlertSettings global, HostAlerts? host)
    {
        if (host?.IgnoreDisks?.Any(m => m.TrimEnd('/', '\\').Equals(disk.Mount.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)) == true)
            return null;
        var limit = host?.DiskFreePercent ?? global.DiskFreePercent;
        return limit > 0 ? limit : null;
    }

    public static double? SsdWearLimit(AlertSettings global, HostAlerts? host)
    {
        var limit = host?.SsdWearPercent ?? global.SsdWearPercent;
        return limit > 0 ? limit : null;
    }
}
