// Services/FanControlService.cs
namespace HomeLabControl.Services;

using HomeLabControl.Models;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

public class FanControlService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HlcConfigService _configService;
    private readonly ILogger<FanControlService> _logger;

    public FanControlService(
        IHttpClientFactory httpClientFactory,
        HlcConfigService configService,
        ILogger<FanControlService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configService = configService;
        _logger = logger;
    }

    private HttpClient CreateHttpClient(Host server)
        => _configService.CreateAgentClient(_httpClientFactory, server, _configService.GetFanControlSettings().ApiTimeoutSeconds);

    /// <summary>Хосты с секцией fanControl и агентом</summary>
    public List<Host> GetServers() => _configService.GetFanControlHosts();

    private Host? FindServer(string serverName) => GetServers().FirstOrDefault(s => s.Name == serverName);

    public async Task<List<SensorInfo>> GetSensorsAsync(string serverName)
    {
        var server = FindServer(serverName);
        if (server == null)
        {
            _logger.LogWarning("Server not found: {ServerName}", serverName);
            return new List<SensorInfo>();
        }

        try
        {
            using var httpClient = CreateHttpClient(server);
            var response = await httpClient.GetAsync($"{server.BaseUrl}/api/Sensors");
            response.EnsureSuccessStatusCode();

            var sensors = await response.Content.ReadFromJsonAsync<List<SensorInfo>>();
            return sensors ?? new List<SensorInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get sensors from {ServerName}", serverName);
            return new List<SensorInfo>();
        }
    }

    public async Task<List<FanInfo>> GetFansAsync(string serverName)
    {
        var server = FindServer(serverName);
        if (server == null)
        {
            _logger.LogWarning("Server not found: {ServerName}", serverName);
            return new List<FanInfo>();
        }

        try
        {
            using var httpClient = CreateHttpClient(server);
            var response = await httpClient.GetAsync($"{server.BaseUrl}/api/Fans");
            response.EnsureSuccessStatusCode();

            var fans = await response.Content.ReadFromJsonAsync<List<FanInfo>>();
            return fans ?? new List<FanInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get fans from {ServerName}", serverName);
            return new List<FanInfo>();
        }
    }

    public async Task<List<ProfileInfo>> GetProfilesAsync(string serverName)
    {
        var server = FindServer(serverName);
        if (server == null)
        {
            _logger.LogWarning("Server not found: {ServerName}", serverName);
            return new List<ProfileInfo>();
        }

        try
        {
            using var httpClient = CreateHttpClient(server);
            var response = await httpClient.GetAsync($"{server.BaseUrl}/api/Profiles");
            response.EnsureSuccessStatusCode();

            var profiles = await response.Content.ReadFromJsonAsync<List<ProfileInfo>>();
            return profiles ?? new List<ProfileInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get profiles from {ServerName}", serverName);
            return new List<ProfileInfo>();
        }
    }

    public async Task<bool> SetFanSpeedAsync(string serverName, string fanId, int speed)
    {
        var server = FindServer(serverName);
        if (server == null)
        {
            _logger.LogWarning("Server not found: {ServerName}", serverName);
            return false;
        }

        try
        {
            using var httpClient = CreateHttpClient(server);
            var response = await httpClient.PutAsJsonAsync(
                $"{server.BaseUrl}/api/Fans/{fanId}/speed",
                new { speed });

            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Set fan {FanId} on {ServerName} to {Speed}%", fanId, serverName, speed);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set fan speed on {ServerName}", serverName);
            return false;
        }
    }

    public async Task<bool> EnableProfileAsync(string serverName, string profileId)
    {
        var server = FindServer(serverName);
        if (server == null) return false;

        try
        {
            using var httpClient = CreateHttpClient(server);
            var response = await httpClient.PutAsync(
                $"{server.BaseUrl}/api/Profiles/{profileId}/enable", null);

            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Enabled profile {ProfileId} on {ServerName}", profileId, serverName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enable profile on {ServerName}", serverName);
            return false;
        }
    }

    public async Task<bool> DisableProfileAsync(string serverName, string profileId)
    {
        var server = FindServer(serverName);
        if (server == null) return false;

        try
        {
            using var httpClient = CreateHttpClient(server);
            var response = await httpClient.PutAsync(
                $"{server.BaseUrl}/api/Profiles/{profileId}/disable", null);

            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Disabled profile {ProfileId} on {ServerName}", profileId, serverName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disable profile on {ServerName}", serverName);
            return false;
        }
    }

    public async Task<bool> SetFanAutoModeAsync(string serverName, string fanId)
    {
        var server = FindServer(serverName);
        if (server == null) return false;

        try
        {
            using var httpClient = CreateHttpClient(server);
            var response = await httpClient.PutAsync(
                $"{server.BaseUrl}/api/Fans/{fanId}/auto", null);

            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Set fan {FanId} on {ServerName} to auto mode", fanId, serverName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set auto mode on {ServerName}", serverName);
            return false;
        }
    }

    public async Task<bool> CreateProfileAsync(string serverName, ProfileInfo profile)
    {
        var server = FindServer(serverName);
        if (server == null) return false;

        try
        {
            var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var httpClient = CreateHttpClient(server);
            var response = await httpClient.PostAsync(
                $"{server.BaseUrl}/api/Profiles", content);

            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Created profile {ProfileName} on {ServerName}", profile.Name, serverName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create profile on {ServerName}", serverName);
            return false;
        }
    }

    public async Task<bool> UpdateProfileAsync(string serverName, string profileId, ProfileInfo profile)
    {
        var server = FindServer(serverName);
        if (server == null) return false;

        try
        {
            var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var httpClient = CreateHttpClient(server);
            var response = await httpClient.PutAsync(
                $"{server.BaseUrl}/api/Profiles/{profileId}", content);

            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Updated profile {ProfileId} on {ServerName}", profileId, serverName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update profile on {ServerName}", serverName);
            return false;
        }
    }

    public async Task<bool> DeleteProfileAsync(string serverName, string profileId)
    {
        var server = FindServer(serverName);
        if (server == null) return false;

        try
        {
            using var httpClient = CreateHttpClient(server);
            var response = await httpClient.DeleteAsync(
                $"{server.BaseUrl}/api/Profiles/{profileId}");

            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Deleted profile {ProfileId} on {ServerName}", profileId, serverName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete profile on {ServerName}", serverName);
            return false;
        }
    }
}
