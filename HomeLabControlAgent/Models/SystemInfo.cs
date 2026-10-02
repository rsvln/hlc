// Models/SystemInfo.cs
namespace HomeLabControlAgent.Models;

/// <summary>Ресурсы машины (GET /api/system): CPU, память, место на дисках, сеть.</summary>
public class SystemInfo
{
    /// <summary>Загрузка CPU за последний интервал замера, % (null — ещё нет двух замеров).</summary>
    public double? CpuPercent { get; set; }

    public long MemoryTotalBytes { get; set; }
    public long MemoryUsedBytes { get; set; }

    public long UptimeSeconds { get; set; }

    public List<DiskSpace> Disks { get; set; } = new();
    public List<NetworkRate> Network { get; set; } = new();

    /// <summary>Когда снят замер (UTC).</summary>
    public DateTime Time { get; set; }
}

public class DiskSpace
{
    /// <summary>Точка монтирования (Linux) или буква диска (Windows, "C:\").</summary>
    public string Mount { get; set; } = string.Empty;

    /// <summary>Устройство (Linux, /dev/sda1) или метка тома (Windows).</summary>
    public string Device { get; set; } = string.Empty;

    public string FileSystem { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
}

public class NetworkRate
{
    public string Name { get; set; } = string.Empty;
    public double RxBytesPerSecond { get; set; }
    public double TxBytesPerSecond { get; set; }
}
