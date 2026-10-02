using HomeLabControl.Models;
using HomeLabControl.Services;
using Xunit;

namespace HomeLabControl.Tests;

public class WolRelayTests
{
    [Theory]
    [InlineData("192.168.12.0/24", "192.168.12.77", true)]
    [InlineData("192.168.12.0/24", "192.168.11.77", false)]
    [InlineData("192.168.12.5/24", "192.168.12.200", true)]   // адрес сети нормализуется
    [InlineData("10.0.0.0/8", "10.200.3.4", true)]
    [InlineData("192.168.12.77", "192.168.12.77", true)]      // без префикса — /32
    [InlineData("192.168.12.77/32", "192.168.12.78", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    public void Subnet_Contains(string cidr, string ip, bool expected)
    {
        Assert.True(IpSubnet.TryParse(cidr, out var subnet));
        Assert.Equal(expected, subnet.Contains(ip));
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.12.0/33")]
    [InlineData("192.168.12/24")]
    [InlineData("nas.local/24")]
    [InlineData("::1/64")]
    public void Subnet_Invalid(string cidr) => Assert.False(IpSubnet.TryParse(cidr, out _));

    private static readonly List<WolRelay> Relays = new()
    {
        new WolRelay { Name = "dacha-wide", Subnet = "192.168.0.0/16", Type = WolRelay.Ssh, Address = "192.168.12.1" },
        new WolRelay { Name = "dacha", Subnet = "192.168.12.0/24", Type = WolRelay.Ssh, Address = "192.168.12.1" },
        new WolRelay { Name = "dacha-ha", Subnet = "192.168.12.0/24", Type = WolRelay.Agent, Host = "ha-dacha" }
    };

    [Fact]
    public void Resolve_MostSpecificSubnet_FirstOfEqual()
        => Assert.Equal("dacha", WolRelayService.Resolve(new Host { Name = "pc", Ip = "192.168.12.20" }, Relays)?.Name);

    [Fact]
    public void Resolve_AgentRelayOnly_SkippedForItsOwnHost()
    {
        var onlyAgent = new List<WolRelay> { Relays[2] };
        Assert.Null(WolRelayService.Resolve(new Host { Name = "ha-dacha", Ip = "192.168.12.77" }, onlyAgent));
        Assert.Equal("dacha-ha", WolRelayService.Resolve(new Host { Name = "pc", Ip = "192.168.12.20" }, onlyAgent)?.Name);
    }

    [Fact]
    public void Resolve_WolViaOverrides_LocalForcesBroadcast()
    {
        Assert.Equal("dacha-ha", WolRelayService.Resolve(new Host { Name = "pc", Ip = "192.168.12.20", Power = new PowerSection { WolVia = "dacha-ha" } }, Relays)?.Name);
        Assert.Null(WolRelayService.Resolve(new Host { Name = "pc", Ip = "192.168.12.20", Power = new PowerSection { WolVia = "local" } }, Relays));
    }

    [Fact]
    public void Resolve_OtherNetwork_NoRelay()
        => Assert.Null(WolRelayService.Resolve(new Host { Name = "lab2", Ip = "10.1.1.22" }, Relays));

    [Theory]
    [InlineData("aa-bb-cc-dd-ee-ff", "AA:BB:CC:DD:EE:FF")]
    [InlineData("AABBCCDDEEFF", "AA:BB:CC:DD:EE:FF")]
    [InlineData("AA:BB:CC:DD:EE:FF; reboot", null)]   // уходит в команду SSH — только hex
    [InlineData("AA:BB:CC:DD:EE", null)]
    public void NormalizeMac(string input, string? expected) => Assert.Equal(expected, WolRelayService.NormalizeMac(input));

    [Fact]
    public void WakeCommand_UsesInterfaceAndFallbacks()
    {
        var cmd = WolRelayService.WakeCommand("br-lan", "AA:BB:CC:DD:EE:FF");
        Assert.Contains("etherwake -i br-lan AA:BB:CC:DD:EE:FF", cmd);
        Assert.Contains("ether-wake -i br-lan", cmd);
        Assert.Contains("wakeonlan AA:BB:CC:DD:EE:FF", cmd);
        Assert.Contains("exit 127", cmd);
    }

    [Fact]
    public void Validate_RelayErrors()
    {
        var config = new HlcConfig
        {
            Hosts = { new Host { Name = "nas", Ip = "192.168.1.10", Power = new PowerSection { WolVia = "nope" } } }
        };
        config.Modules.Power.WolRelays = new()
        {
            new WolRelay { Name = "a", Subnet = "bad", Type = WolRelay.Ssh },
            new WolRelay { Name = "b", Subnet = "10.0.0.0/8", Type = WolRelay.Agent, Host = "nas" },
            new WolRelay { Name = "c", Subnet = "10.0.0.0/8", Type = WolRelay.Ssh, Address = "1.2.3.4; rm" }
        };

        var errors = ConfigValidator.ValidateWolRelays(config);

        Assert.Contains(errors, e => e.Contains("invalid subnet"));
        Assert.Contains(errors, e => e.Contains("needs address or host"));
        Assert.Contains(errors, e => e.Contains("has no agent"));
        Assert.Contains(errors, e => e.Contains("invalid address"));
        Assert.Contains(errors, e => e.Contains("power.wolVia 'nope'"));
    }

    [Fact]
    public void ConfigPatch_AddRelay_KeepsComments()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sample.yaml"));
        var current = HlcConfigService.Deserialize(text);
        var updated = HlcConfigService.Deserialize(HlcConfigService.Serialize(current));
        updated.Modules.Power.WolRelays = new() { new WolRelay { Name = "dacha", Subnet = "192.168.12.0/24", Address = "192.168.12.1" } };

        var patched = HlcConfigService.PatchConfigText(text, current, updated);

        Assert.Equal(HlcConfigService.Serialize(updated), HlcConfigService.Serialize(HlcConfigService.Deserialize(patched)));
        Assert.Equal(text.Split('#').Length, patched.Split('#').Length);
    }
}
