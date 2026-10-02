using System.Net;
using System.Net.Sockets;

namespace HomeLabControl.Services;

/// <summary>IPv4-подсеть "192.168.12.0/24": принадлежность адреса, длина префикса.</summary>
public readonly record struct IpSubnet(uint Network, int PrefixLength)
{
    private uint Mask => PrefixLength == 0 ? 0 : uint.MaxValue << (32 - PrefixLength);

    public static bool TryParse(string? text, out IpSubnet subnet)
    {
        subnet = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Trim().Split('/');
        if (parts.Length is < 1 or > 2 || !TryToUInt(parts[0], out var address))
            return false;

        var prefix = 32;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32))
            return false;

        var mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        subnet = new IpSubnet(address & mask, prefix);
        return true;
    }

    public bool Contains(string? ip) => TryToUInt(ip, out var address) && (address & Mask) == Network;

    private static bool TryToUInt(string? text, out uint value)
    {
        value = 0;
        // Строго 4 октета: IPAddress.TryParse принимает и "192.168.12" (= 192.168.0.12)
        if (text == null || text.Trim().Split('.').Length != 4 ||
            !IPAddress.TryParse(text.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        value = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        return true;
    }

    public override string ToString()
        => $"{Network >> 24}.{(Network >> 16) & 255}.{(Network >> 8) & 255}.{Network & 255}/{PrefixLength}";
}
