// Services/SystemMonitor.cs
namespace HomeLabControlAgent.Services;

using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using HomeLabControlAgent.Models;

/// <summary>
/// Ресурсы машины: CPU, память, место на дисках, сеть. Замер раз в несколько секунд в фоне —
/// загрузка CPU и скорость сети считаются как разница двух замеров. Без LibreHardwareMonitor:
/// Linux — /proc, Windows — WinAPI, диски и сеть — средствами .NET.
/// </summary>
public class SystemMonitor : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly ILogger<SystemMonitor> _logger;
    private volatile SystemInfo _current = new() { Time = DateTime.UtcNow };

    private (ulong Idle, ulong Total)? _lastCpu;
    private Dictionary<string, (long Rx, long Tx)> _lastNet = new();
    private DateTime _lastNetTime;

    public SystemMonitor(ILogger<SystemMonitor> logger)
    {
        _logger = logger;
    }

    public SystemInfo Current => _current;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _current = Sample();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "System resources sample failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private SystemInfo Sample()
    {
        var info = new SystemInfo
        {
            Time = DateTime.UtcNow,
            UptimeSeconds = Environment.TickCount64 / 1000
        };

        // CPU
        var cpu = ReadCpuTimes();
        if (cpu != null)
        {
            if (_lastCpu is { } last)
                info.CpuPercent = CpuPercent(last, cpu.Value);
            _lastCpu = cpu;
        }

        // Память
        var (total, available) = ReadMemory();
        info.MemoryTotalBytes = total;
        info.MemoryUsedBytes = Math.Max(0, total - available);

        info.Disks = ReadDisks();
        info.Network = ReadNetwork(info.Time);
        return info;
    }

    // ─── CPU ──────────────────────────────────────────────────────────────────

    /// <summary>Загрузка CPU между двумя замерами (idle, total), %.</summary>
    public static double CpuPercent((ulong Idle, ulong Total) previous, (ulong Idle, ulong Total) current)
    {
        var total = current.Total - previous.Total;
        var idle = current.Idle - previous.Idle;
        if (current.Total < previous.Total || total == 0)
            return 0;
        return Math.Round(Math.Clamp(100.0 * (total - Math.Min(idle, total)) / total, 0, 100), 1);
    }

    /// <summary>Первая строка /proc/stat ("cpu user nice system idle iowait irq softirq steal …") → (idle+iowait, всего).</summary>
    public static (ulong Idle, ulong Total)? ParseProcStat(string procStat)
    {
        var line = procStat.Split('\n').FirstOrDefault(l => l.StartsWith("cpu "));
        if (line == null)
            return null;

        var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(v => ulong.TryParse(v, out var n) ? n : 0).ToArray();
        if (values.Length < 4)
            return null;

        // guest / guest_nice уже входят в user / nice — не считаем дважды
        var total = values.Take(Math.Min(values.Length, 8)).Aggregate(0UL, (a, b) => a + b);
        var idle = values[3] + (values.Length > 4 ? values[4] : 0);
        return (idle, total);
    }

    private static (ulong Idle, ulong Total)? ReadCpuTimes()
    {
        if (OperatingSystem.IsLinux())
            return ParseProcStat(File.ReadAllText("/proc/stat"));

        if (OperatingSystem.IsWindows() && GetSystemTimes(out var idle, out var kernel, out var user))
        {
            // kernel включает idle
            return (idle.Value, kernel.Value + user.Value);
        }

        return null;
    }

    // ─── Память ───────────────────────────────────────────────────────────────

    /// <summary>/proc/meminfo → (MemTotal, MemAvailable) в байтах.</summary>
    public static (long Total, long Available) ParseMemInfo(string memInfo)
    {
        long Value(string key)
        {
            var line = memInfo.Split('\n').FirstOrDefault(l => l.StartsWith(key + ":"));
            if (line == null)
                return -1;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var kb) ? kb * 1024 : -1;
        }

        var total = Value("MemTotal");
        var available = Value("MemAvailable");
        if (available < 0)
            available = Math.Max(0, Value("MemFree")) + Math.Max(0, Value("Buffers")) + Math.Max(0, Value("Cached"));
        return (Math.Max(0, total), Math.Max(0, available));
    }

    private static (long Total, long Available) ReadMemory()
    {
        if (OperatingSystem.IsLinux())
            return ParseMemInfo(File.ReadAllText("/proc/meminfo"));

        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
                return ((long)status.TotalPhys, (long)status.AvailPhys);
        }

        return (0, 0);
    }

    // ─── Диски ────────────────────────────────────────────────────────────────

    /// <summary>Файловые системы дисков; всё остальное (tmpfs, overlay, proc, сетевые…) не показываем.</summary>
    private static readonly HashSet<string> DiskFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "f2fs", "jfs", "reiserfs", "vfat", "exfat", "ntfs", "ntfs3", "fuseblk", "bcachefs"
    };

    /// <summary>/proc/mounts → (устройство, точка монтирования, ФС) реальных дисков, без повторов одного устройства.</summary>
    public static List<(string Device, string Mount, string FileSystem)> ParseMounts(string procMounts)
    {
        var result = new List<(string, string, string)>();
        var seenDevices = new HashSet<string>();

        foreach (var line in procMounts.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !DiskFileSystems.Contains(parts[2]))
                continue;

            var device = parts[0];
            // \040 — пробел в пути
            var mount = parts[1].Replace("\\040", " ");

            // Bind-монтирования и подтома btrfs того же устройства — показываем один раз (первым идёт основной)
            if (!seenDevices.Add(device))
                continue;

            result.Add((device, mount, parts[2]));
        }

        return result;
    }

    private List<DiskSpace> ReadDisks()
    {
        var disks = new List<DiskSpace>();

        if (OperatingSystem.IsLinux())
        {
            foreach (var (device, mount, fs) in ParseMounts(File.ReadAllText("/proc/mounts")))
            {
                try
                {
                    var drive = new DriveInfo(mount);
                    if (drive.TotalSize > 0)
                        disks.Add(new DiskSpace { Mount = mount, Device = device, FileSystem = fs, TotalBytes = drive.TotalSize, FreeBytes = drive.AvailableFreeSpace });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Disk space of {Mount} unavailable", mount);
                }
            }

            return disks;
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            try
            {
                if (drive.IsReady && drive.TotalSize > 0)
                    disks.Add(new DiskSpace
                    {
                        Mount = drive.Name, Device = drive.VolumeLabel, FileSystem = drive.DriveFormat,
                        TotalBytes = drive.TotalSize, FreeBytes = drive.AvailableFreeSpace
                    });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Disk space of {Drive} unavailable", drive.Name);
            }
        }

        return disks;
    }

    // ─── Сеть ─────────────────────────────────────────────────────────────────

    /// <summary>Виртуальные интерфейсы контейнеров и мостов — шум, не показываем.</summary>
    public static bool IsVirtualInterface(string name)
        => name == "lo" || name.StartsWith("veth") || name.StartsWith("docker") || name.StartsWith("br-") ||
           name.StartsWith("virbr") || name.StartsWith("cni") || name.StartsWith("flannel");

    private List<NetworkRate> ReadNetwork(DateTime now)
    {
        var rates = new List<NetworkRate>();
        var counters = new Dictionary<string, (long Rx, long Tx)>();
        var seconds = (now - _lastNetTime).TotalSeconds;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                IsVirtualInterface(nic.Name))
                continue;

            try
            {
                // Только интерфейсы со своим адресом: на Windows это отсекает NDIS-фильтры (WFP, QoS, Npcap)
                // и порты виртуальных коммутаторов, на Linux — порты мостов (адрес у самого моста)
                if (!HasAddress(nic))
                    continue;

                var stats = nic.GetIPStatistics();
                counters[nic.Name] = (stats.BytesReceived, stats.BytesSent);

                if (_lastNet.TryGetValue(nic.Name, out var last) && seconds > 0 &&
                    stats.BytesReceived >= last.Rx && stats.BytesSent >= last.Tx)
                {
                    rates.Add(new NetworkRate
                    {
                        Name = nic.Name,
                        RxBytesPerSecond = Math.Round((stats.BytesReceived - last.Rx) / seconds),
                        TxBytesPerSecond = Math.Round((stats.BytesSent - last.Tx) / seconds)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Network statistics of {Nic} unavailable", nic.Name);
            }
        }

        _lastNet = counters;
        _lastNetTime = now;
        return rates;
    }

    private static bool HasAddress(NetworkInterface nic)
        => nic.GetIPProperties().UnicastAddresses.Any(a =>
            a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ||
            (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal));

    // ─── WinAPI ───────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
