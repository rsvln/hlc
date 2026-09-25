using HomeLabControl.Models;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HostModel = HomeLabControl.Models.Host;

namespace HomeLabControl.Services;

public class PowerControlService : IDisposable
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HlcConfigService _configService;
    private readonly ILogger<PowerControlService> _logger;
    // Всегда актуальные настройки: HlcConfigService перечитывает HomeLabControl.yaml при изменении
    private PowerControlSettings Settings => _configService.GetPowerSettings();
    private System.Threading.Timer? _statusUpdateTimer;

    public PowerControlService(
        IHttpClientFactory httpClientFactory,
        HlcConfigService configService,
        ILogger<PowerControlService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configService = configService;
        _logger = logger;

        // Запускаем автоматическое обновление статусов
        StartStatusUpdates();
    }

    private HttpClient CreateHttpClient(string ip, int port)
    {
        var host = _configService.FindHost(ip, port);
        return _configService.CreateAgentClient(_httpClientFactory, host, Settings.ApiTimeoutSeconds);
    }

    // ─── Configuration ────────────────────────────────────────────────────────

    public List<HostModel> GetHosts() => _configService.GetPowerHosts();

    public PowerControlSettings GetSettings() => Settings;

    public void ReloadConfig() => _configService.Reload();

    // ─── Host Status Updates ──────────────────────────────────────────────────

    private void StartStatusUpdates()
    {
        var interval = TimeSpan.FromSeconds(Settings.PingIntervalSeconds);
        _statusUpdateTimer = new System.Threading.Timer(
            _ => UpdateAllHostStatuses(),
            null,
            TimeSpan.Zero,      // Start immediately
            interval);

        _logger.LogInformation("Started host status updates every {Interval}s",
            Settings.PingIntervalSeconds);
    }

    private void UpdateAllHostStatuses()
    {
        foreach (var host in GetHosts())
        {
            _ = UpdateHostStatusAsync(host); // Fire and forget
        }
    }

    private async Task UpdateHostStatusAsync(HostModel host)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host.Ip, Settings.PingTimeoutMs);
            host.IsOnline = reply.Status == IPStatus.Success;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ping failed for {Host}", host.Name);
            host.IsOnline = false;
        }
    }

    // ─── Wake-on-LAN ──────────────────────────────────────────────────────────

    public async Task<bool> WakeOnLanAsync(string macAddress)
    {
        try
        {
            // Parse MAC address
            var macClean = macAddress.Replace(":", "").Replace("-", "");
            if (macClean.Length != 12)
            {
                _logger.LogError("Invalid MAC address format: {Mac}", macAddress);
                return false;
            }

            var macBytes = Enumerable.Range(0, macClean.Length / 2)
                .Select(x => Convert.ToByte(macClean.Substring(x * 2, 2), 16))
                .ToArray();

            // Build WOL magic packet: 6 bytes of 0xFF + 16x MAC address
            var packet = new byte[102];
            for (int i = 0; i < 6; i++)
                packet[i] = 0xFF;

            for (int i = 1; i <= 16; i++)
                Array.Copy(macBytes, 0, packet, i * 6, 6);

            // Send to port 9 (standard WOL port)
            await SendWolPacketAsync(packet, Settings.WolPort);

            // Also send to port 7 (alternative WOL port)
            await SendWolPacketAsync(packet, 7);

            _logger.LogInformation("Sent WOL packet to {Mac}", macAddress);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WOL packet");
            return false;
        }
    }

    private async Task SendWolPacketAsync(byte[] packet, int port)
    {
        using var client = new UdpClient();
        client.EnableBroadcast = true;
        var broadcastAddress = IPAddress.Parse(Settings.BroadcastAddress);
        var endpoint = new IPEndPoint(broadcastAddress, port);
        await client.SendAsync(packet, packet.Length, endpoint);
    }

    // ─── Shutdown ─────────────────────────────────────────────────────────────

    public async Task<bool> ShutdownAsync(string ip, int port, int delay, bool withDialog)
    {
        try
        {
            var endpoint = withDialog
                ? "shutdown-with-dialog"
                : "shutdown";

            var url = $"http://{ip}:{port}/api/power/{endpoint}?delay={delay}";

            _logger.LogInformation("Calling shutdown: {Url}", url);

            using var httpClient = CreateHttpClient(ip, port);
            var response = await httpClient.PostAsync(url, null);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Shutdown initiated for {Ip}", ip);
                return true;
            }
            else
            {
                _logger.LogWarning("Shutdown failed: {Status}", response.StatusCode);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shutdown request failed for {Ip}", ip);
            return false;
        }
    }

    // ─── Reboot ───────────────────────────────────────────────────────────────

    public async Task<bool> RebootAsync(string ip, int port, int delay, bool withDialog)
    {
        try
        {
            var endpoint = withDialog
                ? "reboot-with-dialog"
                : "reboot";

            var url = $"http://{ip}:{port}/api/power/{endpoint}?delay={delay}";

            _logger.LogInformation("Calling reboot: {Url}", url);

            using var httpClient = CreateHttpClient(ip, port);
            var response = await httpClient.PostAsync(url, null);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Reboot initiated for {Ip}", ip);
                return true;
            }
            else
            {
                _logger.LogWarning("Reboot failed: {Status}", response.StatusCode);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reboot request failed for {Ip}", ip);
            return false;
        }
    }

    // ─── Cancel ───────────────────────────────────────────────────────────────

    public async Task<bool> CancelAsync(string ip, int port)
    {
        try
        {
            var url = $"http://{ip}:{port}/api/power/cancel";

            _logger.LogInformation("Calling cancel: {Url}", url);

            using var httpClient = CreateHttpClient(ip, port);
            var response = await httpClient.PostAsync(url, null);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Cancelled shutdown/reboot for {Ip}", ip);
                return true;
            }
            else
            {
                _logger.LogWarning("Cancel failed: {Status}", response.StatusCode);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cancel request failed for {Ip}", ip);
            return false;
        }
    }

    // ─── Cleanup ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _statusUpdateTimer?.Dispose();
    }
}
