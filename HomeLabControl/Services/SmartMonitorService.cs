// Services/SmartMonitorService.cs
namespace HomeLabControl.Services;

using HomeLabControl.Models;
using System.Net.Http.Json;
using System.Text.Json;

public class SmartMonitorService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HlcConfigService _configService;
    private readonly ILogger<SmartMonitorService> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public SmartMonitorService(
        IHttpClientFactory httpClientFactory,
        HlcConfigService configService,
        ILogger<SmartMonitorService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configService = configService;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Servers / Config
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Хосты с секцией smart и агентом (HomeLabControl.yaml).</summary>
    public List<Host> GetServers() => _configService.GetSmartHosts();

    // ──────────────────────────────────────────────────────────────────────────
    // API calls
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<List<SmartDiskInfo>> GetDisksAsync(string serverName)
    {
        try
        {
            var server = GetServer(serverName);
            var url = $"{server.BaseUrl}/api/smart/disks";
            using var client = CreateClient(server);
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GET {Url} returned {Code}", url, response.StatusCode);
                return new();
            }

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<SmartDiskInfo>>(json, _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get SMART disks from {Server}", serverName);
            return new();
        }
    }

    public async Task<SmartDiskInfo?> GetDiskAsync(string serverName, string diskId)
    {
        try
        {
            var server = GetServer(serverName);
            var url = $"{server.BaseUrl}/api/smart/disks/{diskId}";
            using var client = CreateClient(server);
            var json = await client.GetStringAsync(url);
            return JsonSerializer.Deserialize<SmartDiskInfo>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get disk {DiskId} from {Server}", diskId, serverName);
            return null;
        }
    }

    public async Task<bool> RefreshAsync(string serverName)
    {
        try
        {
            var server = GetServer(serverName);
            var url = $"{server.BaseUrl}/api/smart/refresh";
            using var client = CreateClient(server);
            var response = await client.PostAsync(url, null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh SMART on {Server}", serverName);
            return false;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────

    private Host GetServer(string serverName)
        => GetServers().FirstOrDefault(s =>
               s.Name.Equals(serverName, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidOperationException($"Server '{serverName}' not found in config");

    // SMART-скан на агенте может идти десятки секунд (smartctl по каждому диску)
    private HttpClient CreateClient(Host server)
        => _configService.CreateAgentClient(_httpClientFactory, server, timeoutSeconds: 120);
}