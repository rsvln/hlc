using HomeLabControlAgent.Services;
using Xunit;

namespace HomeLabControlAgent.Tests;

public class WakeOnLanTests
{
    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("aa-bb-cc-dd-ee-ff")]
    [InlineData("aabb.ccdd.eeff")]
    [InlineData("AABBCCDDEEFF")]
    public void ParseMac_AcceptsCommonFormats(string mac)
    {
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }, WakeOnLan.ParseMac(mac));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AA:BB:CC:DD:EE")]
    [InlineData("GG:BB:CC:DD:EE:FF")]
    [InlineData("AA:BB:CC:DD:EE:FF; rm -rf /")]
    public void ParseMac_RejectsGarbage(string? mac)
    {
        Assert.Null(WakeOnLan.ParseMac(mac));
    }

    [Fact]
    public void MagicPacket_SixFfThenMacSixteenTimes()
    {
        var mac = new byte[] { 1, 2, 3, 4, 5, 6 };
        var packet = WakeOnLan.MagicPacket(mac);

        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));
        for (var i = 1; i <= 16; i++)
            Assert.Equal(mac, packet.Skip(i * 6).Take(6));
    }
}
