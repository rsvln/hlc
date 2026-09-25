// Models/SmartDisk.cs
namespace HomeLabControlAgent.Models;

/// <summary>
/// Полная SMART информация о диске
/// </summary>
public class SmartDisk
{
    public string Id { get; set; } = "";              // "sda", "nvme0n1", "PhysicalDrive0"
    public string DevicePath { get; set; } = "";      // "/dev/sda", "//./PhysicalDrive0"
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Firmware { get; set; } = "";
    public string Protocol { get; set; } = "";        // "ATA", "NVMe", "SCSI", "SAT"
    public string Type { get; set; } = "";            // "sat", "nvme", "scsi"
    public string CapacityHuman { get; set; } = "";   // "2.0 TB"
    public long CapacityBytes { get; set; }

    // Буквы дисков / точки монтирования
    // Windows: ["C:", "D:"]   Linux: ["/", "/home"]
    public List<string> MountPoints { get; set; } = new();

    // Состояние
    public SmartHealthStatus Health { get; set; } = SmartHealthStatus.Unknown;
    public string? SmartStatus { get; set; }
    public bool SmartEnabled { get; set; } = true;

    // Температура
    public double? Temperature { get; set; }
    public double? TemperatureMax { get; set; }

    // Статистика эксплуатации
    public int? PowerOnHours { get; set; }
    public int? PowerCycleCount { get; set; }

    // Объём записи/чтения
    public long? TotalBytesRead { get; set; }
    public long? TotalBytesWritten { get; set; }
    public string? TotalReadHuman { get; set; }
    public string? TotalWrittenHuman { get; set; }

    // SSD/NVMe wear
    public int? PercentUsed { get; set; }
    public int? AvailableSpare { get; set; }

    // ATA атрибуты (HDD/SATA SSD)
    public List<SmartAttribute> Attributes { get; set; } = new();

    // NVMe health log
    public NvmeHealthInfo? NvmeHealth { get; set; }

    public DateTime ScannedAt { get; set; } = DateTime.UtcNow;
    public string? Error { get; set; }
}

public class SmartAttribute
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Value { get; set; }
    public int Worst { get; set; }
    public int Threshold { get; set; }
    public long Raw { get; set; }
    public string RawString { get; set; } = "";
    public bool PreFail { get; set; }
    public AttributeStatus Status { get; set; } = AttributeStatus.Ok;
}

public class NvmeHealthInfo
{
    public int? CriticalWarning { get; set; }
    public int? AvailableSpare { get; set; }
    public int? AvailableSpareThreshold { get; set; }
    public int? PercentageUsed { get; set; }
    public long? DataUnitsRead { get; set; }
    public long? DataUnitsWritten { get; set; }
    public int? MediaErrors { get; set; }
    public int? NumErrLogEntries { get; set; }
    public int? PowerCycles { get; set; }
    public long? PowerOnHours { get; set; }
    public int? UnsafeShutdowns { get; set; }
}

public enum SmartHealthStatus { Passed, Failed, Unknown }
public enum AttributeStatus { Ok, Warning, Failed }