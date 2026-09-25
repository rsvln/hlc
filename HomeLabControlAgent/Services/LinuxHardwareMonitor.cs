// Services/LinuxHardwareMonitor.cs
namespace HomeLabControlAgent.Services;

using HomeLabControlAgent.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;

public class LinuxHardwareMonitor : IHardwareMonitor
{
    private const string HwmonPath = "/sys/class/hwmon";

    // Отфильтровываем явно мусорные значения неподключённых термодатчиков (AUXTIN и т.п.)
    private const double MinValidTemp = -40;
    private const double MaxValidTemp = 150;

    private static readonly TimeSpan DiskTempTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NvidiaTtl = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<LinuxHardwareMonitor> _logger;
    private readonly Dictionary<string, HwmonDevice> _devices = new();
    private readonly List<DiskDevice> _disks = new();
    private readonly object _lock = new();

    // pwmN_enable до того, как агент взял вентилятор под ручное управление
    private readonly Dictionary<string, string> _originalPwmEnable = new();

    private NvidiaGpuData? _nvidiaGpu;
    private DateTime _nvidiaUpdatedAt = DateTime.MinValue;
    private bool _nvidiaAvailable = true;

    public LinuxHardwareMonitor(ILogger<LinuxHardwareMonitor> logger)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("LinuxHardwareMonitor only works on Linux");

        _logger = logger;
        DiscoverDevices();
        DiscoverDisks();
        UpdateNvidiaGpu(force: true);
        _logger.LogInformation("Linux hwmon initialized, found {DeviceCount} devices, {DiskCount} disks",
            _devices.Count, _disks.Count);
    }

    private void DiscoverDevices()
    {
        if (!Directory.Exists(HwmonPath))
        {
            _logger.LogWarning("hwmon path not found: {Path}", HwmonPath);
            return;
        }

        // Сортируем, чтобы при одинаковых именах (nvme, drivetemp, ...) порядок был стабильным
        foreach (var hwmonDir in Directory.GetDirectories(HwmonPath).OrderBy(d => d, StringComparer.Ordinal))
        {
            try
            {
                var name = ReadFile(Path.Combine(hwmonDir, "name"))?.Trim() ?? Path.GetFileName(hwmonDir);

                // Первое устройство сохраняет "чистое" имя (совместимость с существующими профилями),
                // остальные с тем же именем получают суффикс hwmonN — раньше они просто затирали друг друга
                var key = _devices.ContainsKey(name) ? $"{name}-{Path.GetFileName(hwmonDir)}" : name;

                var device = new HwmonDevice
                {
                    Path = hwmonDir,
                    Name = key
                };

                foreach (var tempFile in Directory.GetFiles(hwmonDir, "temp*_input"))
                {
                    var match = Regex.Match(Path.GetFileName(tempFile), @"^temp(\d+)_input$");
                    if (!match.Success)
                        continue;

                    var index = match.Groups[1].Value;
                    var label = ReadFile(Path.Combine(hwmonDir, $"temp{index}_label"))?.Trim();

                    device.TempSensors.Add(new HwmonSensor
                    {
                        Index = index,
                        InputFile = tempFile,
                        Label = string.IsNullOrEmpty(label) ? $"temp{index}" : label
                    });
                }

                foreach (var fanFile in Directory.GetFiles(hwmonDir, "fan*_input"))
                {
                    var match = Regex.Match(Path.GetFileName(fanFile), @"^fan(\d+)_input$");
                    if (!match.Success)
                        continue;

                    var index = match.Groups[1].Value;
                    var pwmFile = Path.Combine(hwmonDir, $"pwm{index}");
                    var label = ReadFile(Path.Combine(hwmonDir, $"fan{index}_label"))?.Trim();

                    device.Fans.Add(new HwmonFan
                    {
                        Index = index,
                        InputFile = fanFile,
                        PwmFile = File.Exists(pwmFile) ? pwmFile : null,
                        Label = string.IsNullOrEmpty(label) ? $"fan{index}" : label
                    });
                }

                _devices[key] = device;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process hwmon device: {Dir}", hwmonDir);
            }
        }
    }

    private void DiscoverDisks()
    {
        try
        {
            var blockDevices = Directory.GetFiles("/dev", "sd*")
                .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^sd[a-z]+$"))
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToList();

            foreach (var device in blockDevices)
            {
                try
                {
                    // -i не будит диск из standby
                    var modelInfo = RunProcess("smartctl", $"-i {device}") ?? string.Empty;
                    var modelMatch = Regex.Match(modelInfo, @"(?:Device Model|Model Number|Product):\s+(.+)");
                    var model = modelMatch.Success ? modelMatch.Groups[1].Value.Trim() : Path.GetFileName(device);

                    _disks.Add(new DiskDevice
                    {
                        DevicePath = device,
                        Name = Path.GetFileName(device),
                        Model = model
                    });

                    _logger.LogInformation("Found disk: {Device} ({Model})", device, model);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get info for disk: {Device}", device);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover disks");
        }
    }

    private void UpdateNvidiaGpu(bool force = false)
    {
        // Раньше nvidia-smi запускался на каждой итерации ProfileEngine (2 раза в секунду),
        // а на машинах без драйвера каждый раз падал с исключением
        if (!_nvidiaAvailable)
            return;
        if (!force && DateTime.UtcNow - _nvidiaUpdatedAt < NvidiaTtl)
            return;

        _nvidiaUpdatedAt = DateTime.UtcNow;

        try
        {
            var output = RunProcess("nvidia-smi",
                "--query-gpu=temperature.gpu,fan.speed,name --format=csv,noheader,nounits");

            var parts = output?.Trim().Split(',').Select(p => p.Trim()).ToArray();
            if (parts is { Length: >= 3 })
            {
                _nvidiaGpu = new NvidiaGpuData
                {
                    Name = parts[2],
                    Temperature = double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var temp) ? temp : 0,
                    FanSpeed = int.TryParse(parts[1], out var fan) ? fan : 0
                };
            }
            else
            {
                _nvidiaGpu = null;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // nvidia-smi не установлен — больше не пытаемся
            _nvidiaAvailable = false;
            _nvidiaGpu = null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "nvidia-smi failed");
            _nvidiaGpu = null;
        }
    }

    /// <summary>
    /// Температура диска с кэшем. "-n standby" — не будить спящий HDD
    /// (раньше smartctl -A дёргался на каждый запрос датчиков и не давал дискам уснуть).
    /// Для спящего диска отдаём последнее известное значение.
    /// </summary>
    private double? GetDiskTemperature(DiskDevice disk)
    {
        if (DateTime.UtcNow - disk.TempUpdatedAt < DiskTempTtl)
            return disk.LastTemperature;

        disk.TempUpdatedAt = DateTime.UtcNow;

        try
        {
            var output = RunProcess("smartctl", $"-n standby -A {disk.DevicePath}");
            if (string.IsNullOrEmpty(output))
                return disk.LastTemperature;

            // Temperature_Celsius (ID 194): RAW_VALUE — 10-я колонка
            var tempMatch = Regex.Match(output, @"^\s*194\s+Temperature_Celsius(?:\s+\S+){7}\s+(\d+)", RegexOptions.Multiline);
            if (tempMatch.Success && double.TryParse(tempMatch.Groups[1].Value, out var temp))
                return disk.LastTemperature = temp;

            // Альтернативно Airflow_Temperature_Cel (ID 190)
            var airflowMatch = Regex.Match(output, @"^\s*190\s+Airflow_Temperature_Cel(?:\s+\S+){7}\s+(\d+)", RegexOptions.Multiline);
            if (airflowMatch.Success && double.TryParse(airflowMatch.Groups[1].Value, out var airflowTemp))
                return disk.LastTemperature = airflowTemp;

            // SCSI/SAS: "Current Drive Temperature:     35 C"
            var scsiMatch = Regex.Match(output, @"Current Drive Temperature:\s+(\d+)");
            if (scsiMatch.Success && double.TryParse(scsiMatch.Groups[1].Value, out var scsiTemp))
                return disk.LastTemperature = scsiTemp;

            return disk.LastTemperature;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get temperature for disk: {Device}", disk.DevicePath);
            return disk.LastTemperature;
        }
    }

    /// <summary>Запуск внешней утилиты с таймаутом. stderr не перенаправляем — иначе возможен дедлок на полном буфере.</summary>
    private string? RunProcess(string fileName, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();

        if (!process.WaitForExit(ProcessTimeout))
        {
            _logger.LogWarning("{File} {Args} timed out", fileName, arguments);
            try { process.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
            return null;
        }

        return outputTask.GetAwaiter().GetResult();
    }

    public void Update()
    {
        UpdateNvidiaGpu();
    }

    public List<Sensor> GetSensors()
    {
        var result = new List<Sensor>();

        // Hwmon sensors
        foreach (var device in _devices.Values)
        {
            foreach (var tempSensor in device.TempSensors)
            {
                var value = ReadIntFile(tempSensor.InputFile);
                if (!value.HasValue)
                    continue;

                var celsius = value.Value / 1000.0;
                if (celsius < MinValidTemp || celsius > MaxValidTemp)
                    continue;

                result.Add(new Sensor
                {
                    Id = $"{device.Name}_temp{tempSensor.Index}",
                    Name = $"{device.Name} - {tempSensor.Label}",
                    Type = SensorType.Temperature,
                    Value = celsius,
                    Unit = "°C"
                });
            }
        }

        // Nvidia GPU
        var gpu = _nvidiaGpu;
        if (gpu != null)
        {
            result.Add(new Sensor
            {
                Id = "nvidia_gpu0_temp0",
                Name = gpu.Name,
                Type = SensorType.Temperature,
                Value = gpu.Temperature,
                Unit = "°C"
            });
        }

        // HDD temperatures
        lock (_lock)
        {
            foreach (var disk in _disks)
            {
                var temp = GetDiskTemperature(disk);
                if (!temp.HasValue)
                    continue;

                result.Add(new Sensor
                {
                    Id = $"disk_{disk.Name}_temp",
                    Name = $"{disk.Name} ({disk.Model})",
                    Type = SensorType.Temperature,
                    Value = temp.Value,
                    Unit = "°C"
                });
            }
        }

        return result;
    }

    public List<FanController> GetFans()
    {
        var result = new List<FanController>();

        foreach (var device in _devices.Values)
        {
            foreach (var fan in device.Fans)
            {
                var currentSpeed = ReadIntFile(fan.InputFile) ?? 0;
                var currentPercent = 0;
                var isAuto = false;

                if (fan.PwmFile != null)
                {
                    var pwmValue = ReadIntFile(fan.PwmFile);
                    if (pwmValue.HasValue)
                        currentPercent = (int)Math.Round(pwmValue.Value / 255.0 * 100);

                    // 0 = full speed, 1 = manual, 2+ = автоматические режимы чипа/BIOS
                    var mode = ReadFile(fan.PwmFile + "_enable")?.Trim();
                    isAuto = mode != null && mode != "0" && mode != "1";
                }

                result.Add(new FanController
                {
                    Id = $"{device.Name}_{fan.Index}",
                    Name = $"{device.Name} - {fan.Label}",
                    CurrentSpeed = currentSpeed,
                    CurrentSpeedPercent = currentPercent,
                    MinSpeed = 0,
                    MaxSpeed = 100,
                    SupportsControl = fan.PwmFile != null,
                    ControlType = ControlType.PWM,
                    IsAutoMode = isAuto
                });
            }
        }

        var gpu = _nvidiaGpu;
        if (gpu != null)
        {
            result.Add(new FanController
            {
                Id = "nvidia_gpu0_fan0",
                Name = "nvidia_gpu0 - fan0",
                CurrentSpeed = 0,                     // nvidia-smi не даёт RPM
                CurrentSpeedPercent = gpu.FanSpeed,   // Это проценты
                MinSpeed = 0,
                MaxSpeed = 100,
                SupportsControl = false,
                ControlType = ControlType.PWM,
                IsAutoMode = true
            });
        }

        return result;
    }

    public Sensor? GetSensor(string sensorId)
    {
        return GetSensors().FirstOrDefault(s => s.Id == sensorId);
    }

    public FanController? GetFan(string fanId)
    {
        return GetFans().FirstOrDefault(f => f.Id == fanId);
    }

    public bool SetFanSpeed(string fanId, int speedPercent)
    {
        try
        {
            var fan = FindControllableFan(fanId);
            if (fan == null)
                return false;

            var pwmEnableFile = fan.PwmFile + "_enable";
            if (File.Exists(pwmEnableFile))
            {
                lock (_lock)
                {
                    if (!_originalPwmEnable.ContainsKey(fanId))
                        _originalPwmEnable[fanId] = ReadFile(pwmEnableFile)?.Trim() ?? "2";
                }

                if (ReadFile(pwmEnableFile)?.Trim() != "1")
                    File.WriteAllText(pwmEnableFile, "1");
            }

            var pwmValue = (int)Math.Round(Math.Clamp(speedPercent, 0, 100) / 100.0 * 255);
            File.WriteAllText(fan.PwmFile!, pwmValue.ToString());

            _logger.LogDebug("Set fan {FanId} to {Speed}% (PWM: {Pwm})", fanId, speedPercent, pwmValue);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set fan speed for {FanId}", fanId);
            return false;
        }
    }

    public bool RestoreAutoMode(string fanId)
    {
        try
        {
            var fan = FindControllableFan(fanId);
            if (fan == null)
                return false;

            var pwmEnableFile = fan.PwmFile + "_enable";
            if (!File.Exists(pwmEnableFile))
                return false;

            string mode;
            lock (_lock)
            {
                // Если до агента был ручной режим (1) — отдаём в автоматический (2)
                mode = _originalPwmEnable.TryGetValue(fanId, out var original) && original != "1" ? original : "2";
                _originalPwmEnable.Remove(fanId);
            }

            File.WriteAllText(pwmEnableFile, mode);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore auto mode for {FanId}", fanId);
            return false;
        }
    }

    /// <summary>
    /// Id вентилятора: "{device}_{index}". Имя устройства само может содержать "_"
    /// (asus_wmi_sensors, dell_smm), поэтому режем по последнему подчёркиванию.
    /// </summary>
    private HwmonFan? FindControllableFan(string fanId)
    {
        var separator = fanId.LastIndexOf('_');
        if (separator <= 0)
            return null;

        var deviceName = fanId[..separator];
        var fanIndex = fanId[(separator + 1)..].Replace("fan", "");

        if (!_devices.TryGetValue(deviceName, out var device))
        {
            _logger.LogWarning("Device not found: {DeviceName}", deviceName);
            return null;
        }

        var fan = device.Fans.FirstOrDefault(f => f.Index == fanIndex);
        if (fan?.PwmFile == null)
        {
            _logger.LogWarning("PWM control not available for fan: {FanId}", fanId);
            return null;
        }

        return fan;
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadIntFile(string path)
    {
        var content = ReadFile(path);
        return int.TryParse(content?.Trim(), out var value) ? value : null;
    }

    public void Dispose()
    {
        _logger.LogInformation("Linux hwmon disposed");
    }

    private class NvidiaGpuData
    {
        public string Name { get; set; } = string.Empty;
        public double Temperature { get; set; }
        public int FanSpeed { get; set; }
    }

    private class DiskDevice
    {
        public string DevicePath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public double? LastTemperature { get; set; }
        public DateTime TempUpdatedAt { get; set; } = DateTime.MinValue;
    }

    private class HwmonDevice
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<HwmonSensor> TempSensors { get; set; } = new();
        public List<HwmonFan> Fans { get; set; } = new();
    }

    private class HwmonSensor
    {
        public string Index { get; set; } = string.Empty;
        public string InputFile { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }

    private class HwmonFan
    {
        public string Index { get; set; } = string.Empty;
        public string InputFile { get; set; } = string.Empty;
        public string? PwmFile { get; set; }
        public string Label { get; set; } = string.Empty;
    }
}
