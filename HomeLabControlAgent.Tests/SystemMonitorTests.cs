using HomeLabControlAgent.Services;
using Xunit;

namespace HomeLabControlAgent.Tests;

public class SystemMonitorTests
{
    [Fact]
    public void ProcStat_IdleIncludesIowait_TotalSkipsGuest()
    {
        // user nice system idle iowait irq softirq steal guest guest_nice
        var stat = "cpu  100 0 50 800 50 0 0 0 30 0\ncpu0 50 0 25 400 25 0 0 0 15 0\nintr 1\n";

        var times = SystemMonitor.ParseProcStat(stat);

        Assert.NotNull(times);
        Assert.Equal(850UL, times!.Value.Idle);
        Assert.Equal(1000UL, times.Value.Total); // guest (30) уже входит в user
    }

    [Fact]
    public void ProcStat_Garbage_ReturnsNull()
    {
        Assert.Null(SystemMonitor.ParseProcStat("intr 1 2 3\n"));
    }

    [Fact]
    public void CpuPercent_FromTwoSamples()
    {
        // за интервал: всего 200 тиков, из них простой 50 → 75%
        Assert.Equal(75.0, SystemMonitor.CpuPercent((Idle: 850, Total: 1000), (Idle: 900, Total: 1200)));
    }

    [Fact]
    public void CpuPercent_CounterReset_IsZero()
    {
        Assert.Equal(0, SystemMonitor.CpuPercent((Idle: 900, Total: 1200), (Idle: 10, Total: 20)));
        Assert.Equal(0, SystemMonitor.CpuPercent((Idle: 900, Total: 1200), (Idle: 900, Total: 1200)));
    }

    [Fact]
    public void MemInfo_UsesMemAvailable()
    {
        var memInfo = "MemTotal:       16000000 kB\nMemFree:         1000000 kB\nMemAvailable:    6000000 kB\nBuffers:          100000 kB\nCached:          2000000 kB\n";

        var (total, available) = SystemMonitor.ParseMemInfo(memInfo);

        Assert.Equal(16000000L * 1024, total);
        Assert.Equal(6000000L * 1024, available);
    }

    [Fact]
    public void MemInfo_OldKernelWithoutMemAvailable_FreePlusCache()
    {
        var memInfo = "MemTotal:       16000000 kB\nMemFree:         1000000 kB\nBuffers:          100000 kB\nCached:          2000000 kB\n";

        var (_, available) = SystemMonitor.ParseMemInfo(memInfo);

        Assert.Equal(3100000L * 1024, available);
    }

    [Fact]
    public void Mounts_OnlyDiskFileSystems_OnePerDevice()
    {
        var mounts = string.Join('\n',
            "sysfs /sys sysfs rw 0 0",
            "proc /proc proc rw 0 0",
            "tmpfs /run tmpfs rw 0 0",
            "/dev/nvme0n1p2 / ext4 rw,relatime 0 0",
            "/dev/nvme0n1p1 /boot/efi vfat rw 0 0",
            "/dev/md0 /srv/md0 ext4 rw 0 0",
            "/dev/md0 /var/lib/docker/volumes/x ext4 rw 0 0",   // bind того же устройства
            "overlay /var/lib/docker/overlay2/abc/merged overlay rw 0 0",
            "/dev/sdb1 /mnt/my\\040disk btrfs rw 0 0",
            "//nas/share /mnt/nas cifs rw 0 0",
            "tank/data /tank/data zfs rw 0 0");

        var disks = SystemMonitor.SelectDisks(SystemMonitor.ParseMounts(mounts), _ => true);

        Assert.Equal(new[] { "/", "/boot/efi", "/mnt/my disk", "/srv/md0", "/tank/data" }, disks.Select(d => d.Mount));
        Assert.Equal("/dev/md0", disks[3].Device);
        Assert.Equal("btrfs", disks[2].FileSystem);
    }

    [Fact]
    public void Mounts_InContainer_FileBindMountsSkipped_ShortestDirectoryWins()
    {
        // Docker монтирует файлы хоста (resolv.conf, hosts) с того же устройства, что и тома
        var mounts = string.Join('\n',
            "overlay / overlay rw 0 0",
            "/dev/sdd /etc/resolv.conf ext4 rw 0 0",
            "/dev/sdd /etc/hosts ext4 rw 0 0",
            "/dev/sdd /data/volume ext4 rw 0 0",
            "/dev/sdd /data ext4 rw 0 0");
        var files = new HashSet<string> { "/etc/resolv.conf", "/etc/hosts" };

        var disks = SystemMonitor.SelectDisks(SystemMonitor.ParseMounts(mounts), m => !files.Contains(m));

        Assert.Single(disks);
        Assert.Equal("/data", disks[0].Mount);
    }

    [Theory]
    [InlineData("lo", true)]
    [InlineData("veth1a2b3c", true)]
    [InlineData("docker0", true)]
    [InlineData("br-5f3e2a", true)]
    [InlineData("eth0", false)]
    [InlineData("enp3s0", false)]
    [InlineData("Ethernet", false)]
    public void VirtualInterfaces_AreSkipped(string name, bool isVirtual)
    {
        Assert.Equal(isVirtual, SystemMonitor.IsVirtualInterface(name));
    }
}
