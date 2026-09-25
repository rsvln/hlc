// Services/SmartMonitorService.cs
namespace HomeLabControlAgent.Services;

using HomeLabControlAgent.Models;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// Кросс-платформенный SMART монитор через smartctl -j (JSON output).
/// Работает на Linux и Windows (при наличии smartmontools).
/// </summary>
public class SmartMonitorService : ISmartMonitorService
{
    private readonly ILogger<SmartMonitorService> _logger;
    private readonly int _scanIntervalSeconds;
    private readonly string _smartctlPath;

    private List<SmartDisk> _cache = new();
    private DateTime _lastScan = DateTime.MinValue;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private static readonly TimeSpan SmartctlTimeout = TimeSpan.FromSeconds(30);

    public SmartMonitorService(ILogger<SmartMonitorService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _scanIntervalSeconds = configuration.GetValue("SmartMonitor:ScanIntervalSeconds", 300);
        _smartctlPath = ResolveSmartctlPath(configuration);
        _logger.LogInformation("smartctl resolved to: {Path}", _smartctlPath);
    }

    private static string ResolveSmartctlPath(IConfiguration configuration)
    {
        var configured = configuration["SmartMonitor:SmartctlPath"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        if (OperatingSystem.IsWindows())
        {
            var exeDir = AppContext.BaseDirectory;
            var next = Path.Combine(exeDir, "smartctl.exe");
            if (File.Exists(next)) return next;

            var candidates = new[]
            {
                @"C:\Program Files\smartmontools\bin\smartctl.exe",
                @"C:\Program Files (x86)\smartmontools\bin\smartctl.exe",
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
        }

        return "smartctl";
    }

    public async Task<List<SmartDisk>> GetAllDisksAsync()
    {
        if (_cache.Count > 0 && (DateTime.UtcNow - _lastScan).TotalSeconds < _scanIntervalSeconds)
            return _cache;

        // Кэш пуст (первый запрос) — дожидаемся идущего скана, а не отдаём пустой список
        await RefreshInternalAsync(waitIfRunning: _cache.Count == 0);
        return _cache;
    }

    public async Task<SmartDisk?> GetDiskAsync(string diskId)
    {
        var disks = await GetAllDisksAsync();
        return disks.FirstOrDefault(d => d.Id == diskId);
    }

    public Task RefreshAsync() => RefreshInternalAsync(waitIfRunning: false);

    private async Task RefreshInternalAsync(bool waitIfRunning)
    {
        if (waitIfRunning)
        {
            await _scanLock.WaitAsync();
            if (_cache.Count > 0 && (DateTime.UtcNow - _lastScan).TotalSeconds < _scanIntervalSeconds)
            {
                _scanLock.Release();
                return;
            }
        }
        else if (!await _scanLock.WaitAsync(0))
        {
            _logger.LogDebug("SMART scan already in progress, skipping");
            return;
        }

        try
        {
            _logger.LogInformation("Starting SMART scan...");
            var devices = await DiscoverDevicesAsync();
            var disks = new List<SmartDisk>();

            foreach (var device in devices)
            {
                var disk = await ScanDiskAsync(device.Path, device.Type);
                if (disk != null)
                    disks.Add(disk);
            }

            // Обогащаем точками монтирования / буквами дисков
            EnrichMountPoints(disks);

            _cache = disks;
            _lastScan = DateTime.UtcNow;
            _logger.LogInformation("SMART scan complete: {Count} disks found", disks.Count);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Mount Points / Drive Letters
    // ──────────────────────────────────────────────────────────────────────────

    private void EnrichMountPoints(List<SmartDisk> disks)
    {
        if (OperatingSystem.IsWindows())
            EnrichMountPointsWindows(disks);
        else if (OperatingSystem.IsLinux())
            EnrichMountPointsLinux(disks);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void EnrichMountPointsWindows(List<SmartDisk> disks)
    {
        try
        {
            using var diskSearcher = new System.Management.ManagementObjectSearcher(
                "SELECT DeviceID FROM Win32_DiskDrive");

            foreach (System.Management.ManagementObject drive in diskSearcher.Get())
            {
                var wmiDeviceId = drive["DeviceID"]?.ToString(); // \.\PHYSICALDRIVE0
                if (string.IsNullOrEmpty(wmiDeviceId)) continue;

                // Извлекаем индекс из WMI: \.\PHYSICALDRIVE2 → 2
                var wmiIndexMatch = System.Text.RegularExpressions.Regex.Match(
                    wmiDeviceId, @"\d+$");
                if (!wmiIndexMatch.Success) continue;
                var wmiIndex = int.Parse(wmiIndexMatch.Value);

                // smartctl на Windows может возвращать как //./PhysicalDrive2 так и /dev/sdc
                // Для обоих вычисляем числовой индекс и сравниваем с WMI
                SmartDisk? smartDisk = disks.FirstOrDefault(d => GetDiskIndex(d.DevicePath) == wmiIndex);
                if (smartDisk == null) continue;

                // Заменяем /dev/sdX на нормальный Windows путь
                smartDisk.DevicePath = wmiDeviceId.Replace(@"\", "/");

                using var partQuery = new System.Management.ManagementObjectSearcher(
                    "ASSOCIATORS OF {Win32_DiskDrive.DeviceID='" + wmiDeviceId + "'} " +
                    "WHERE AssocClass=Win32_DiskDriveToDiskPartition");

                foreach (System.Management.ManagementObject partition in partQuery.Get())
                {
                    var partId = partition["DeviceID"]?.ToString();
                    if (string.IsNullOrEmpty(partId)) continue;

                    using var logicalQuery = new System.Management.ManagementObjectSearcher(
                        "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + partId + "'} " +
                        "WHERE AssocClass=Win32_LogicalDiskToPartition");

                    foreach (System.Management.ManagementObject logical in logicalQuery.Get())
                    {
                        var driveLetter = logical["DeviceID"]?.ToString(); // "C:"
                        if (!string.IsNullOrEmpty(driveLetter) &&
                            !smartDisk.MountPoints.Contains(driveLetter))
                        {
                            smartDisk.MountPoints.Add(driveLetter);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich drive letters via WMI");
        }
    }

    /// Возвращает числовой индекс диска из любого формата пути:
    ///   //./PhysicalDrive2  → 2
    ///   \.\PHYSICALDRIVE2  → 2
    ///   /dev/sda            → 0  (a=0, b=1, c=2 ...)
    ///   /dev/sdc            → 2
    private static int GetDiskIndex(string devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return -1;

        // PhysicalDrive-стиль: берём число в конце
        var numMatch = System.Text.RegularExpressions.Regex.Match(
            devicePath, @"physicaldrive(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (numMatch.Success)
            return int.Parse(numMatch.Groups[1].Value);

        // /dev/sdX — буква в конце: sda=0, sdb=1, sdc=2
        var sdMatch = System.Text.RegularExpressions.Regex.Match(
            devicePath, @"/dev/sd([a-z])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (sdMatch.Success)
            return sdMatch.Groups[1].Value.ToLowerInvariant()[0] - 'a';

        // /dev/nvme0n1 — берём первое число
        var nvmeMatch = System.Text.RegularExpressions.Regex.Match(devicePath, @"nvme(\d+)");
        if (nvmeMatch.Success)
            return int.Parse(nvmeMatch.Groups[1].Value);

        return -1;
    }


    private void EnrichMountPointsLinux(List<SmartDisk> disks)
    {
        // Для каждого диска вызываем lsblk по конкретному устройству (/dev/sda).
        // Это надёжнее чем матчинг по серийнику (который требует root).
        foreach (var disk in disks)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "lsblk",
                    Arguments = "-J -o NAME,MOUNTPOINT " + disk.DevicePath,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = System.Diagnostics.Process.Start(psi)!;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(10_000);

                if (string.IsNullOrWhiteSpace(output)) continue;

                var root = System.Text.Json.Nodes.JsonNode.Parse(output);
                var blockdevices = root?["blockdevices"]?.AsArray();
                if (blockdevices == null) continue;

                void Walk(System.Text.Json.Nodes.JsonNode? node)
                {
                    if (node == null) return;
                    var mp = node["mountpoint"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(mp) && !disk.MountPoints.Contains(mp))
                        disk.MountPoints.Add(mp);
                    var children = node["children"]?.AsArray();
                    if (children != null)
                        foreach (var child in children)
                            Walk(child);
                }

                foreach (var dev in blockdevices)
                    Walk(dev);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get mount points for {Device}", disk.DevicePath);
            }
        }
    }


    private record DeviceEntry(string Path, string Type);

    private async Task<List<DeviceEntry>> DiscoverDevicesAsync()
    {
        var devices = new List<DeviceEntry>();

        foreach (var args in new[] { "--scan-open -j", "--scan -j" })
        {
            var (json, _) = await RunSmartctlAsync(args);
            if (string.IsNullOrWhiteSpace(json)) continue;

            try
            {
                var root = JsonNode.Parse(json);
                var arr = root?["devices"]?.AsArray();
                if (arr == null || arr.Count == 0) continue;

                foreach (var item in arr)
                {
                    var name = item?["name"]?.GetValue<string>();
                    var type = item?["type"]?.GetValue<string>() ?? "auto";
                    if (!string.IsNullOrEmpty(name))
                        devices.Add(new DeviceEntry(name, type));
                }

                if (devices.Count > 0) break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse smartctl {Args} output", args);
            }
        }

        // Windows fallback
        if (devices.Count == 0 && OperatingSystem.IsWindows())
        {
            _logger.LogInformation("smartctl --scan returned no devices, probing PhysicalDrive0..15");
            for (int i = 0; i <= 15; i++)
            {
                var path = $"//./PhysicalDrive{i}";
                var (infoJson, exitCode) = await RunSmartctlAsync($"-i -j {path}");

                if (exitCode == 1 || string.IsNullOrWhiteSpace(infoJson)) continue;

                try
                {
                    var root = JsonNode.Parse(infoJson);
                    var messages = root?["smartctl"]?["messages"]?.AsArray();
                    if (messages != null && messages.Any(e =>
                        e?["string"]?.GetValue<string>()?.Contains("No such device") == true))
                        break;

                    var proto = root?["device"]?["type"]?.GetValue<string>() ?? "auto";
                    devices.Add(new DeviceEntry(path, proto));
                    _logger.LogDebug("Found via probe: {Path} ({Proto})", path, proto);
                }
                catch
                {
                    devices.Add(new DeviceEntry(path, "auto"));
                }
            }
        }

        _logger.LogDebug("Discovered {Count} devices total", devices.Count);
        return devices;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Disk Scanning
    // ──────────────────────────────────────────────────────────────────────────

    private async Task<SmartDisk?> ScanDiskAsync(string devicePath, string deviceType)
    {
        var (json, exitCode) = await RunSmartctlAsync($"-a -j {devicePath}");

        if (string.IsNullOrWhiteSpace(json))
        {
            _logger.LogWarning("smartctl returned empty output for {Device}", devicePath);
            return null;
        }

        if (exitCode == 1)
        {
            _logger.LogWarning("smartctl syntax error for {Device} (exit {Code})", devicePath, exitCode);
            return null;
        }

        try
        {
            return ParseSmartJson(json, devicePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse SMART data for {Device}", devicePath);
            return new SmartDisk
            {
                Id = DeviceToId(devicePath),
                DevicePath = devicePath,
                Error = ex.Message,
                ScannedAt = DateTime.UtcNow
            };
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // JSON Parsing
    // ──────────────────────────────────────────────────────────────────────────

    private SmartDisk ParseSmartJson(string json, string devicePath)
    {
        var root = JsonNode.Parse(json)!;

        var disk = new SmartDisk
        {
            Id = DeviceToId(devicePath),
            DevicePath = devicePath,
            Model = root["model_name"]?.GetValue<string>()?.Trim() ?? "",
            SerialNumber = root["serial_number"]?.GetValue<string>()?.Trim() ?? "",
            Firmware = root["firmware_version"]?.GetValue<string>()?.Trim() ?? "",
            Protocol = root["device"]?["protocol"]?.GetValue<string>() ?? "",
            Type = root["device"]?["type"]?.GetValue<string>() ?? "",
            ScannedAt = DateTime.UtcNow
        };

        var capacityBytes = root["user_capacity"]?["bytes"]?.GetValue<long>() ?? 0;
        disk.CapacityBytes = capacityBytes;
        disk.CapacityHuman = FormatBytes(capacityBytes);

        var smartPassed = root["smart_status"]?["passed"]?.GetValue<bool>();
        if (smartPassed.HasValue)
        {
            disk.Health = smartPassed.Value ? SmartHealthStatus.Passed : SmartHealthStatus.Failed;
            disk.SmartStatus = smartPassed.Value ? "PASSED" : "FAILED";
        }
        else
        {
            disk.Health = SmartHealthStatus.Unknown;
        }

        disk.Temperature = root["temperature"]?["current"]?.GetValue<double>();
        disk.PowerOnHours = root["power_on_time"]?["hours"]?.GetValue<int>();
        disk.PowerCycleCount = root["power_cycle_count"]?.GetValue<int>();

        var attrTable = root["ata_smart_attributes"]?["table"]?.AsArray();
        if (attrTable != null)
            ParseAtaAttributes(disk, attrTable);

        var nvmeLog = root["nvme_smart_health_information_log"];
        if (nvmeLog != null)
            ParseNvmeHealth(disk, nvmeLog);

        return disk;
    }

    private void ParseAtaAttributes(SmartDisk disk, JsonArray table)
    {
        foreach (var item in table)
        {
            if (item == null) continue;

            var attr = new SmartAttribute
            {
                Id = item["id"]?.GetValue<int>() ?? 0,
                Name = item["name"]?.GetValue<string>() ?? "",
                Value = item["value"]?.GetValue<int>() ?? 0,
                Worst = item["worst"]?.GetValue<int>() ?? 0,
                Threshold = item["thresh"]?.GetValue<int>() ?? 0,
                Raw = item["raw"]?["value"]?.GetValue<long>() ?? 0,
                RawString = item["raw"]?["string"]?.GetValue<string>() ?? "",
                PreFail = item["flags"]?["prefailure"]?.GetValue<bool>() ?? false
            };

            attr.Status = DetermineAttrStatus(attr);
            EnrichAttrFromId(disk, attr);
            disk.Attributes.Add(attr);
        }
    }

    private static AttributeStatus DetermineAttrStatus(SmartAttribute attr)
    {
        if (attr.PreFail && attr.Threshold > 0 && attr.Value <= attr.Threshold)
            return AttributeStatus.Failed;

        if (attr.PreFail && attr.Threshold > 0 && attr.Value <= attr.Threshold + 10)
            return AttributeStatus.Warning;

        var criticalIds = new[] { 5, 10, 184, 187, 188, 196, 197, 198, 201 };
        if (criticalIds.Contains(attr.Id) && attr.Raw > 0)
            return attr.Raw > 100 ? AttributeStatus.Failed : AttributeStatus.Warning;

        return AttributeStatus.Ok;
    }

    private static void EnrichAttrFromId(SmartDisk disk, SmartAttribute attr)
    {
        switch (attr.Id)
        {
            case 190 or 194:
                if (!disk.Temperature.HasValue && attr.Raw > 0 && attr.Raw < 150)
                    disk.Temperature = attr.Raw & 0xFF;
                break;
            case 241:
                var bw = attr.Raw * 512L;
                if (bw > 0) { disk.TotalBytesWritten = bw; disk.TotalWrittenHuman = FormatBytes(bw); }
                break;
            case 242:
                var br = attr.Raw * 512L;
                if (br > 0) { disk.TotalBytesRead = br; disk.TotalReadHuman = FormatBytes(br); }
                break;
            case 177:
                disk.PercentUsed = 100 - attr.Value;
                break;
            case 232:
                disk.AvailableSpare = attr.Value;
                break;
        }
    }

    private void ParseNvmeHealth(SmartDisk disk, JsonNode nvme)
    {
        var health = new NvmeHealthInfo
        {
            CriticalWarning = nvme["critical_warning"]?.GetValue<int>(),
            AvailableSpare = nvme["available_spare"]?.GetValue<int>(),
            AvailableSpareThreshold = nvme["available_spare_threshold"]?.GetValue<int>(),
            PercentageUsed = nvme["percentage_used"]?.GetValue<int>(),
            DataUnitsRead = nvme["data_units_read"]?.GetValue<long>(),
            DataUnitsWritten = nvme["data_units_written"]?.GetValue<long>(),
            MediaErrors = nvme["media_errors"]?.GetValue<int>(),
            NumErrLogEntries = nvme["num_err_log_entries"]?.GetValue<int>(),
            PowerCycles = nvme["power_cycles"]?.GetValue<int>(),
            PowerOnHours = nvme["power_on_hours"]?.GetValue<long>(),
            UnsafeShutdowns = nvme["unsafe_shutdowns"]?.GetValue<int>()
        };

        disk.NvmeHealth = health;
        disk.PercentUsed = health.PercentageUsed;
        disk.AvailableSpare = health.AvailableSpare;

        if (health.PowerOnHours.HasValue && !disk.PowerOnHours.HasValue)
            disk.PowerOnHours = (int)health.PowerOnHours.Value;
        if (health.PowerCycles.HasValue)
            disk.PowerCycleCount = health.PowerCycles.Value;

        if (health.DataUnitsWritten.HasValue)
        {
            disk.TotalBytesWritten = health.DataUnitsWritten.Value * 512 * 1000;
            disk.TotalWrittenHuman = FormatBytes(disk.TotalBytesWritten.Value);
        }
        if (health.DataUnitsRead.HasValue)
        {
            disk.TotalBytesRead = health.DataUnitsRead.Value * 512 * 1000;
            disk.TotalReadHuman = FormatBytes(disk.TotalBytesRead.Value);
        }

        if (health.CriticalWarning > 0 ||
            (health.AvailableSpare.HasValue && health.AvailableSpareThreshold.HasValue &&
             health.AvailableSpare <= health.AvailableSpareThreshold))
        {
            disk.Health = SmartHealthStatus.Failed;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private async Task<(string Output, int ExitCode)> RunSmartctlAsync(string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _smartctlPath,
                Arguments = arguments,
                // stderr не перенаправляем: непрочитанный stderr при заполнении буфера вешает процесс
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi)!;
            using var timeout = new CancellationTokenSource(SmartctlTimeout);
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                return (output, process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                // Зависший диск не должен навсегда держать _scanLock
                _logger.LogWarning("smartctl {Args} timed out after {Timeout}", arguments, SmartctlTimeout);
                try { process.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
                return (string.Empty, -1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to run smartctl {Args}", arguments);
            return (string.Empty, -1);
        }
    }

    private static string DeviceToId(string path)
        => Path.GetFileName(path.TrimEnd('/'))
               .Replace("\\", "").Replace(".", "").Replace("/", "");

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        int i = 0;
        double value = bytes;
        while (value >= 1000 && i < units.Length - 1) { value /= 1000; i++; }
        return $"{value:F1} {units[i]}";
    }
}
