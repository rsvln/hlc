// Models/SmartDiskInfo.cs
namespace HomeLabControl.Models;

public class SmartDiskInfo
{
    public string Id { get; set; } = "";
    public string DevicePath { get; set; } = "";
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Firmware { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string Type { get; set; } = "";
    public string CapacityHuman { get; set; } = "";
    public long CapacityBytes { get; set; }
    public List<string> MountPoints { get; set; } = new();

    public SmartHealthStatus Health { get; set; } = SmartHealthStatus.Unknown;
    public string? SmartStatus { get; set; }
    public bool SmartEnabled { get; set; } = true;

    public double? Temperature { get; set; }
    public int? PowerOnHours { get; set; }
    public int? PowerCycleCount { get; set; }

    public long? TotalBytesRead { get; set; }
    public long? TotalBytesWritten { get; set; }
    public string? TotalReadHuman { get; set; }
    public string? TotalWrittenHuman { get; set; }

    public int? PercentUsed { get; set; }
    public int? AvailableSpare { get; set; }

    public List<SmartAttributeInfo> Attributes { get; set; } = new();
    public NvmeHealthInfo? NvmeHealth { get; set; }

    public DateTime ScannedAt { get; set; }
    public string? Error { get; set; }

    public int? PowerOnDays => PowerOnHours.HasValue ? PowerOnHours / 24 : null;
    public string HealthBadgeClass => Health switch
    {
        SmartHealthStatus.Passed => "bg-success",
        SmartHealthStatus.Failed => "bg-danger",
        _ => "bg-secondary"
    };
    public string HealthLabel => Health switch
    {
        SmartHealthStatus.Passed => "PASSED",
        SmartHealthStatus.Failed => "FAILED",
        _ => "UNKNOWN"
    };
    public bool IsNvme => Protocol?.Contains("NVMe", StringComparison.OrdinalIgnoreCase) == true;
    public bool HasWarnings => Attributes.Any(a => a.Status != AttributeStatus.Ok);
    public string MountPointsLabel => MountPoints.Count > 0 ? string.Join(" ", MountPoints) : "";
}

public class SmartAttributeInfo
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

    public string StatusBadgeClass => Status switch
    {
        AttributeStatus.Failed => "text-danger fw-bold",
        AttributeStatus.Warning => "text-warning fw-bold",
        _ => ""
    };
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