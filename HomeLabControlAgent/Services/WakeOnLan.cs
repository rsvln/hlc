// Services/WakeOnLan.cs
namespace HomeLabControlAgent.Services;

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

/// <summary>
/// Wake-on-LAN из сети агента: агент — ретранслятор для машин своей подсети
/// (широковещательный пакет не проходит через маршрутизатор / VPN от HomeLabControl).
/// </summary>
public static class WakeOnLan
{
    /// <summary>"AA:BB:CC:DD:EE:FF" / "AA-BB-…" / "AABBCC…" → 6 байт; null — не MAC.</summary>
    public static byte[]? ParseMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
            return null;
        var hex = mac.Replace(":", "").Replace("-", "").Replace(".", "").Trim();
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit))
            return null;
        return Enumerable.Range(0, 6).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
    }

    /// <summary>Magic packet: 6 × 0xFF и 16 повторов MAC.</summary>
    public static byte[] MagicPacket(byte[] mac)
    {
        var packet = new byte[102];
        for (var i = 0; i < 6; i++)
            packet[i] = 0xFF;
        for (var i = 1; i <= 16; i++)
            Array.Copy(mac, 0, packet, i * 6, 6);
        return packet;
    }

    /// <summary>Широковещательные адреса: 255.255.255.255 и направленный broadcast каждой поднятой IPv4-сети.</summary>
    public static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var a in nic.GetIPProperties().UnicastAddresses)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork || a.IPv4Mask == null)
                    continue;
                var ip = a.Address.GetAddressBytes();
                var mask = a.IPv4Mask.GetAddressBytes();
                yield return new IPAddress(ip.Select((b, i) => (byte)(b | ~mask[i])).ToArray());
            }
        }
    }

    /// <summary>Отправить magic packet на все широковещательные адреса, порты 9 и 7. Возвращает число адресов.</summary>
    public static async Task<int> SendAsync(byte[] mac, IEnumerable<IPAddress>? targets = null)
    {
        var packet = MagicPacket(mac);
        var addresses = (targets ?? BroadcastAddresses()).Distinct().ToList();
        using var client = new UdpClient();
        client.EnableBroadcast = true;
        foreach (var address in addresses)
        {
            await client.SendAsync(packet, packet.Length, new IPEndPoint(address, 9));
            await client.SendAsync(packet, packet.Length, new IPEndPoint(address, 7));
        }
        return addresses.Count;
    }
}
