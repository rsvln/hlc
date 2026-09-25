using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using HomeLabControl.Models;
using Host = HomeLabControl.Models.Host;

namespace HomeLabControl.Services;

public enum AgentState
{
    Unknown,
    /// <summary>Агент отвечает и принимает ключ HLC.</summary>
    Online,
    /// <summary>Отвечает, но ключи на агенте не настроены — API открыт всем.</summary>
    Open,
    /// <summary>Отвечает, но ключ HLC не принят (нет в конфиге или не совпадает).</summary>
    KeyRejected,
    /// <summary>Старый агент без /api/agent/info (до версионирования) — работает, но нужно обновить.</summary>
    Legacy,
    Offline
}

public class AgentStatus
{
    public AgentState State { get; set; } = AgentState.Unknown;
    public string? Version { get; set; }
    public string? BuildDate { get; set; }
    public string? Os { get; set; }
    public string? Hostname { get; set; }
    public TimeSpan? Uptime { get; set; }
    public string? Error { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;

    public string StateText => State switch
    {
        AgentState.Online => "online",
        AgentState.Open => "online, no API keys (open)",
        AgentState.KeyRejected => "API key rejected",
        AgentState.Legacy => "legacy agent (update)",
        AgentState.Offline => "offline",
        _ => "unknown"
    };
}

/// <summary>
/// Здоровье и версии агентов. Новый агент отдаёт GET /api/agent/info (без аутентификации);
/// старый (до версионирования) — 404, тогда проверяем /api/power/status и помечаем как Legacy.
/// </summary>
public class AgentStatusService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HlcConfigService _configService;
    private readonly ILogger<AgentStatusService> _logger;
    private readonly ConcurrentDictionary<string, AgentStatus> _cache = new();

    public AgentStatusService(IHttpClientFactory httpClientFactory, HlcConfigService configService, ILogger<AgentStatusService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configService = configService;
        _logger = logger;
    }

    // Ключ — ip:port, а не ip: на одной машине может быть несколько агентов
    public AgentStatus? GetCached(Host host) => _cache.TryGetValue(host.BaseUrl, out var status) ? status : null;

    public void Forget(Host host) => _cache.TryRemove(host.BaseUrl, out _);

    public Task RefreshAllAsync()
        => Task.WhenAll(_configService.GetHosts().Where(h => h.Agent != null).Select(RefreshAsync));

    public async Task<AgentStatus> RefreshAsync(Host host)
    {
        var status = await QueryAsync(host);
        _cache[host.BaseUrl] = status;
        return status;
    }

    private async Task<AgentStatus> QueryAsync(Host host)
    {
        if (!host.HasAgent)
            return new AgentStatus { State = AgentState.Unknown, Error = "agent port is not set" };

        using var client = _configService.CreateAgentClient(_httpClientFactory, host, timeoutSeconds: 3);

        try
        {
            var response = await client.GetAsync($"{host.BaseUrl}/api/agent/info");

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Старый агент: /api/agent/info нет, но /api/power/status есть с первых версий
                var legacy = await client.GetAsync($"{host.BaseUrl}/api/power/status");
                return legacy.IsSuccessStatusCode
                    ? new AgentStatus { State = AgentState.Legacy }
                    : new AgentStatus { State = AgentState.Offline, Error = $"HTTP {(int)legacy.StatusCode}" };
            }

            response.EnsureSuccessStatusCode();
            var info = await response.Content.ReadFromJsonAsync<AgentInfoDto>();

            return new AgentStatus
            {
                State = info == null ? AgentState.Unknown
                    : !info.AuthRequired ? AgentState.Open
                    : info.KeyAccepted ? AgentState.Online
                    : AgentState.KeyRejected,
                Version = info?.Version,
                BuildDate = info?.BuildDate,
                Os = info?.Os,
                Hostname = info?.Hostname,
                Uptime = info != null ? TimeSpan.FromSeconds(info.UptimeSeconds) : null
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new AgentStatus { State = AgentState.Offline, Error = ex.Message };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Agent status query failed for {Host}", host.Name);
            return new AgentStatus { State = AgentState.Unknown, Error = ex.Message };
        }
    }

    private class AgentInfoDto
    {
        public string? Version { get; set; }
        public string? BuildDate { get; set; }
        public string? Os { get; set; }
        public string? Hostname { get; set; }
        public long UptimeSeconds { get; set; }
        public bool AuthRequired { get; set; }
        public bool KeyAccepted { get; set; }
    }
}
