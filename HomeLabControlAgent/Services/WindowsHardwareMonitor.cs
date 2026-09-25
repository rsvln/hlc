// Services/WindowsHardwareMonitor.cs
namespace HomeLabControlAgent.Services;

using HomeLabControlAgent.Models;
using LibreHardwareMonitor.Hardware;
using System.Management;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class WindowsHardwareMonitor : IHardwareMonitor
{
    private readonly Computer _computer;
    private readonly ILogger<WindowsHardwareMonitor> _logger;
    private readonly Dictionary<string, IHardware> _hardwareMap = new();
    private readonly Dictionary<string, ISensor> _sensorMap = new();
    private readonly Dictionary<int, DiskInfo> _disksByIndex = new();

    private readonly object _lock = new();
    private readonly object _updateLock = new();

    public WindowsHardwareMonitor(ILogger<WindowsHardwareMonitor> logger)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WindowsHardwareMonitor only works on Windows");

        _logger = logger;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsStorageEnabled = true
        };

        _computer.Open();
        WaitForStorageDevices(maxWaitMs: 5000);
        LoadDiskInfo();
        Update();

        _logger.LogInformation("LibreHardwareMonitor initialized");
    }

    private void WaitForStorageDevices(int maxWaitMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var storageCount = 0;

        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            var currentCount = _computer.Hardware
                .Count(h => h.HardwareType == HardwareType.Storage);

            if (currentCount > storageCount)
            {
                storageCount = currentCount;
                Thread.Sleep(500); // Подождать еще немного после обнаружения нового диска
            }
            else if (storageCount > 0)
            {
                break; // Все диски найдены
            }

            Thread.Sleep(100);
        }

        _logger.LogDebug("Found {Count} storage devices after {Ms}ms",
            storageCount, sw.ElapsedMilliseconds);
    }

    private void LoadDiskInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Index, Model, SerialNumber FROM Win32_DiskDrive");

            foreach (ManagementObject disk in searcher.Get())
            {
                var index = Convert.ToInt32(disk["Index"]);
                var model = disk["Model"]?.ToString()?.Trim();
                var serial = disk["SerialNumber"]?.ToString()?.Trim();

                if (!string.IsNullOrEmpty(model) && !string.IsNullOrEmpty(serial))
                {
                    var cleanModel = model.Replace("WDC ", "").Replace("ATA ", "").Trim();

                    _disksByIndex[index] = new DiskInfo
                    {
                        Index = index,
                        Model = cleanModel,
                        Serial = serial
                    };

                    //_logger.LogDebug("Found disk #{Index}: {Model} (SN: {Serial})", index, cleanModel, serial);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load disk info from WMI");
        }
    }

    public void Update()
    {
        // Опрос железа вне _lock — медленная операция. Но LibreHardwareMonitor не потокобезопасен,
        // а Update() зовут и ProfileEngine, и HTTP-запросы — сериализуем опросы отдельным lock
        lock (_updateLock)
        {
            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();
                foreach (var subhardware in hardware.SubHardware)
                    subhardware.Update();
            }
        }

        // Rebuild maps под lock — быстро, нет окна с пустой map
        lock (_lock)
        {
            _hardwareMap.Clear();
            _sensorMap.Clear();

            foreach (var hardware in _computer.Hardware)
            {
                MapHardware(hardware);
                foreach (var subhardware in hardware.SubHardware)
                    MapHardware(subhardware);
            }
        }
    }

    private void MapHardware(IHardware hardware)
    {
        var hwId = GetHardwareId(hardware);
        _hardwareMap[hwId] = hardware;

        foreach (var sensor in hardware.Sensors)
        {
            var sensorId = GetSensorId(sensor);
            _sensorMap[sensorId] = sensor;
        }
    }

    public List<Sensor> GetSensors()
    {
        lock (_lock)
        {
            var result = new List<Sensor>();

            foreach (var kvp in _sensorMap)
            {
                var sensor = kvp.Value;

                if (sensor.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature)
                {
                    // Пропустить пороговые значения SMART
                    if (sensor.Name.Contains("Warning") ||
                        sensor.Name.Contains("Critical"))
                    {
                        continue;
                    }

                    var hardwareName = sensor.Hardware.Name;

                    if (sensor.Hardware.HardwareType == HardwareType.Storage)
                    {
                        var serial = FindSerialForHardware(sensor.Hardware);
                        if (!string.IsNullOrEmpty(serial))
                            hardwareName = $"{hardwareName}-{serial}";
                    }

                    result.Add(new Sensor
                    {
                        Id = kvp.Key,
                        Name = $"{hardwareName} - {sensor.Name}",
                        Type = HomeLabControlAgent.Models.SensorType.Temperature,
                        Value = sensor.Value ?? 0,
                        Unit = "°C"
                    });
                }
            }

            return result;
        }
    }

    private string? FindSerialForHardware(IHardware hardware)
    {
        // ДИАГНОСТИКА
        //_logger.LogInformation("=== Hardware: {Name}, Type: {Type}, ID: {Id}",
        //    hardware.Name,
        //    hardware.HardwareType,
        //    hardware.Identifier);

        // Пытаемся извлечь серийник прямо из имени (для некоторых Storage он уже там)
        var nameMatch = System.Text.RegularExpressions.Regex.Match(
            hardware.Name,
            @"-([A-F0-9_\.]+)\.\s*$");

        if (nameMatch.Success)
        {
            var serialFromName = nameMatch.Groups[1].Value;
            //_logger.LogInformation("  -> Found serial in name: {Serial}", serialFromName);
            return serialFromName;
        }

        // Старая логика с WMI (для SATA через материнку)
        var identifier = hardware.Identifier.ToString();
        var match = System.Text.RegularExpressions.Regex.Match(identifier, @"/(\d+)$");

        if (match.Success && int.TryParse(match.Groups[1].Value, out var index))
        {
            //_logger.LogInformation("  -> Index from ID: {Index}", index);

            if (_disksByIndex.TryGetValue(index, out var diskInfo))
            {
                //_logger.LogInformation("  -> WMI disk: {Model}, Serial: {Serial}",
                //    diskInfo.Model, diskInfo.Serial);

                var cleanHwName = hardware.Name.Replace("WDC ", "").Replace("ATA ", "").Trim();
                if (diskInfo.Model.Equals(cleanHwName, StringComparison.OrdinalIgnoreCase) ||
                    diskInfo.Model.Contains(cleanHwName) ||
                    cleanHwName.Contains(diskInfo.Model))
                {
                    return diskInfo.Serial;
                }
            }
        }

        // Fallback по имени
        var modelName = hardware.Name.Replace("WDC ", "").Replace("ATA ", "").Trim();
        foreach (var diskInfo in _disksByIndex.Values)
        {
            if (diskInfo.Model.Equals(modelName, StringComparison.OrdinalIgnoreCase) ||
                diskInfo.Model.Contains(modelName) ||
                modelName.Contains(diskInfo.Model))
            {
                //_logger.LogInformation("  -> Fallback match: {Model}, Serial: {Serial}",
                //    diskInfo.Model, diskInfo.Serial);
                return diskInfo.Serial;
            }
        }

       // _logger.LogWarning("  -> No serial found for {Name}", hardware.Name);
        return null;
    }

    public List<FanController> GetFans()
    {
        lock (_lock)
        {
            var result = new List<FanController>();

            foreach (var kvp in _sensorMap)
            {
                if (kvp.Value.SensorType == LibreHardwareMonitor.Hardware.SensorType.Fan)
                    result.Add(ToFanController(kvp.Key, kvp.Value));
            }

            return result;
        }
    }

    public Sensor? GetSensor(string sensorId)
    {
        lock (_lock)
        {
            if (!_sensorMap.TryGetValue(sensorId, out var sensor))
                return null;

            return new Sensor
            {
                Id = sensorId,
                Name = $"{sensor.Hardware.Name} - {sensor.Name}",
                Type = Models.SensorType.Temperature,
                Value = sensor.Value ?? 0,
                Unit = "°C"
            };
        }
    }

    public FanController? GetFan(string fanId)
    {
        lock (_lock)
        {
            if (!_sensorMap.TryGetValue(fanId, out var sensor) ||
                sensor.SensorType != LibreHardwareMonitor.Hardware.SensorType.Fan)
                return null;

            return ToFanController(fanId, sensor);
        }
    }

    public bool SetFanSpeed(string fanId, int speedPercent)
    {
        try
        {
            var control = FindControlForFan(fanId);
            if (control == null)
                return false;

            // SetSoftware вне lock — операция с железом
            control.SetSoftware(Math.Clamp(speedPercent, 0, 100));
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
            var control = FindControlForFan(fanId);
            if (control == null)
                return false;

            control.SetDefault();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore auto mode for {FanId}", fanId);
            return false;
        }
    }

    private IControl? FindControlForFan(string fanId)
    {
        lock (_lock)
        {
            if (!_sensorMap.TryGetValue(fanId, out var fanSensor))
            {
                _logger.LogWarning("Fan not found: {FanId}", fanId);
                return null;
            }

            var control = FindControlSensor(fanSensor)?.Control;
            if (control == null)
                _logger.LogWarning("Fan control not available for: {FanId}", fanId);

            return control;
        }
    }

    /// <summary>
    /// Control-датчик для Fan-датчика. У IT8613E и ряда других чипов ISensor.Control у Fan пустой,
    /// поэтому связываем по имени ("Fan #2" ↔ "Fan #2"), но только в пределах одного железа —
    /// иначе "Fan #1" материнки мог совпасть с "Fan #1" видеокарты.
    /// Вызывать под _lock.
    /// </summary>
    private ISensor? FindControlSensor(ISensor fanSensor)
    {
        if (fanSensor.Control != null)
            return fanSensor;

        return _sensorMap.Values.FirstOrDefault(s =>
            s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Control &&
            s.Hardware.Identifier == fanSensor.Hardware.Identifier &&
            s.Name == fanSensor.Name);
    }

    // Вызывать под _lock
    private FanController ToFanController(string fanId, ISensor sensor)
    {
        var control = FindControlSensor(sensor)?.Control;

        return new FanController
        {
            Id = fanId,
            Name = $"{sensor.Hardware.Name} - {sensor.Name}",
            CurrentSpeed = (int)(sensor.Value ?? 0),
            CurrentSpeedPercent = control != null ? (int)control.SoftwareValue : 0,
            MinSpeed = 0,
            MaxSpeed = 100,
            SupportsControl = control != null,
            ControlType = ControlType.PWM,
            IsAutoMode = control?.ControlMode == ControlMode.Default
        };
    }

    private string GetHardwareId(IHardware hardware)
        => $"{hardware.HardwareType}_{hardware.Identifier}".Replace("/", "_");

    private string GetSensorId(ISensor sensor)
        => $"{sensor.Hardware.HardwareType}_{sensor.Identifier}".Replace("/", "_");

    public void Dispose()
    {
        _computer.Close();
        _logger.LogInformation("LibreHardwareMonitor disposed");
    }

    private class DiskInfo
    {
        public int Index { get; set; }
        public string Model { get; set; } = string.Empty;
        public string Serial { get; set; } = string.Empty;
    }
}
