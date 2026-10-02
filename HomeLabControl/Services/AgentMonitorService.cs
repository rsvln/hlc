using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using HomeLabControl.Models;
using Host = HomeLabControl.Models.Host;

namespace HomeLabControl.Services;

/// <summary>Событие для уведомлений (Telegram, MQTT).</summary>
public class HlcEvent
{
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>agentOffline, agentOnline, smart, backupFailed, backupSuccess</summary>
    public string Type { get; set; } = "";

    public string Host { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>Последние показания хоста (для страницы хоста и MQTT).</summary>
public class HostSnapshot
{
    public DateTime Time { get; set; }
    public AgentStatus Status { get; set; } = new();
    public List<SensorInfo> Sensors { get; set; } = new();
    public List<FanInfo> Fans { get; set; } = new();
    public List<SmartDiskInfo> Disks { get; set; } = new();

    /// <summary>CPU / память / диски / сеть (null — агент старше 2.0.9 или недоступен).</summary>
    public SystemInfo? System { get; set; }

    /// <summary>Активные тревоги по порогам: ключ (temp:&lt;id&gt;, disk:&lt;mount&gt;, wear:&lt;serial&gt;) → текст.</summary>
    public Dictionary<string, string> Alerts { get; set; } = new();

    /// <summary>Включённые профили вентиляторов с проблемой (датчик пропал, fail-safe, нет вентилятора).</summary>
    public List<(ProfileInfo Profile, ProfileStatusInfo Status)> ProfileProblems { get; set; } = new();
}

/// <summary>
/// Фоновый опрос агентов (modules.monitoring): статус, датчики, вентиляторы, SMART.
///  - история температур за historyHours — график на странице хоста;
///  - события offline/online (после offlineAfterFailures неудач подряд) и деградации SMART — уведомления;
///  - после каждого опроса Polled — MQTT публикует состояния.
/// </summary>
public class AgentMonitorService : BackgroundService
{
    private static readonly JsonSerializerOptions SmartJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly HlcConfigService _config;
    private readonly AgentStatusService _status;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AgentMonitorService> _logger;

    private readonly ConcurrentDictionary<string, HostSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<string, HostHistory> _history = new();
    private readonly ConcurrentDictionary<string, HostHistory> _resourceHistory = new();
    private readonly ConcurrentDictionary<string, HostHealth> _health = new();

    /// <summary>Новое событие (для NotificationService).</summary>
    public event Action<HlcEvent>? EventRaised;

    /// <summary>Опрос хоста завершён (для MqttService).</summary>
    public event Action<Host, HostSnapshot>? Polled;

    public AgentMonitorService(HlcConfigService config, AgentStatusService status, IHttpClientFactory httpClientFactory, ILogger<AgentMonitorService> logger)
    {
        _config = config;
        _status = status;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public HostSnapshot? GetSnapshot(string hostName) => _snapshots.TryGetValue(hostName, out var s) ? s : null;

    /// <summary>История температур: sensorId → (имя, точки).</summary>
    public Dictionary<string, (string Name, List<(DateTime Time, double Value)> Points)> GetHistory(string hostName)
        => _history.TryGetValue(hostName, out var h) ? h.Snapshot() : new();

    /// <summary>История загрузки CPU и памяти, % (серии "cpu" и "memory").</summary>
    public Dictionary<string, (string Name, List<(DateTime Time, double Value)> Points)> GetResourceHistory(string hostName)
        => _resourceHistory.TryGetValue(hostName, out var h) ? h.Snapshot() : new();

    /// <summary>Сгенерировать событие извне (бэкапы) — одна точка входа для уведомлений.</summary>
    public void Raise(HlcEvent e)
    {
        try
        {
            EventRaised?.Invoke(e);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Event handler failed for {Type}", e.Type);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Даём приложению подняться
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ContinueWith(_ => { });

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _config.GetMonitoring();
            try
            {
                var hosts = _config.GetHosts().Where(h => h.HasAgent).ToList();
                await Task.WhenAll(hosts.Select(h => PollHostAsync(h, settings)));

                // Удалённые из конфига хосты
                foreach (var name in _snapshots.Keys.Except(hosts.Select(h => h.Name)).ToList())
                {
                    _snapshots.TryRemove(name, out _);
                    _history.TryRemove(name, out _);
                    _resourceHistory.TryRemove(name, out _);
                    _health.TryRemove(name, out _);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent monitoring cycle failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, settings.PollIntervalSeconds)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollHostAsync(Host host, MonitoringSettings settings)
    {
        var now = DateTime.Now;
        var status = await _status.RefreshAsync(host);
        var health = _health.GetOrAdd(host.Name, _ => new HostHealth());
        var snapshot = new HostSnapshot { Time = now, Status = status };

        // ─── online / offline с защитой от «дрожания» ─────────────────────────
        var reachable = status.State is AgentState.Online or AgentState.Open or AgentState.Legacy or AgentState.KeyRejected;
        if (reachable)
        {
            if (health.ReportedOffline)
                Raise(new HlcEvent { Type = "agentOnline", Host = host.Name, Title = $"{host.Name} is back online", Message = $"Agent {status.StateText}" });
            health.Failures = 0;
            health.ReportedOffline = false;
        }
        else if (++health.Failures == Math.Max(1, settings.OfflineAfterFailures) && health.WasSeen)
        {
            health.ReportedOffline = true;
            Raise(new HlcEvent
            {
                Type = "agentOffline", Host = host.Name, Title = $"{host.Name} is offline",
                Message = $"No response from {host.BaseUrl} for {health.Failures} checks ({status.Error})"
            });
        }

        if (reachable)
            health.WasSeen = true;

        // ─── Датчики / вентиляторы / SMART (нужен доступ к API) ───────────────
        if (status.State is AgentState.Online or AgentState.Open or AgentState.Legacy)
        {
            using var client = _config.CreateAgentClient(_httpClientFactory, host, _config.GetFanControlSettings().ApiTimeoutSeconds);
            try
            {
                snapshot.Sensors = await client.GetFromJsonAsync<List<SensorInfo>>($"{host.BaseUrl}/api/sensors") ?? new();
                snapshot.Fans = await client.GetFromJsonAsync<List<FanInfo>>($"{host.BaseUrl}/api/fans") ?? new();

                var history = _history.GetOrAdd(host.Name, _ => new HostHistory());
                history.Add(now, snapshot.Sensors, TimeSpan.FromHours(Math.Max(1, settings.HistoryHours)));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Sensors of {Host} unavailable", host.Name);
            }

            if (host.FanControl != null)
                snapshot.ProfileProblems = await CheckProfilesAsync(host, health, client);

            snapshot.System = await GetSystemAsync(host, client);
            if (snapshot.System != null)
            {
                var values = new List<(string, string, double)>();
                if (snapshot.System.CpuPercent is double cpu)
                    values.Add(("cpu", "CPU", cpu));
                if (snapshot.System.MemoryPercent is double memory)
                    values.Add(("memory", "Memory", memory));
                _resourceHistory.GetOrAdd(host.Name, _ => new HostHistory())
                    .AddValues(now, values, TimeSpan.FromHours(Math.Max(1, settings.HistoryHours)));
            }

            if (now - health.LastSmart >= TimeSpan.FromMinutes(Math.Max(1, settings.SmartIntervalMinutes)))
            {
                try
                {
                    using var smartClient = _config.CreateAgentClient(_httpClientFactory, host, 120);
                    var json = await smartClient.GetStringAsync($"{host.BaseUrl}/api/smart/disks");
                    health.Disks = JsonSerializer.Deserialize<List<SmartDiskInfo>>(json, SmartJson) ?? new();
                    health.LastSmart = now;
                    CheckSmart(host, health);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SMART of {Host} unavailable", host.Name);
                }
            }
        }

        snapshot.Disks = health.Disks;
        if (status.State is AgentState.Online or AgentState.Open or AgentState.Legacy)
            CheckThresholds(host, health, snapshot);
        snapshot.Alerts = new Dictionary<string, string>(health.Alerts);
        _snapshots[host.Name] = snapshot;

        try
        {
            Polled?.Invoke(host, snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Polled handler failed for {Host}", host.Name);
        }
    }

    /// <summary>
    /// Пороги: температура датчиков, свободное место, износ SSD. Тревога шлётся один раз и снимается
    /// (с событием «back to normal»), только когда значение вернулось за порог с запасом (гистерезис).
    /// </summary>
    private void CheckThresholds(Host host, HostHealth health, HostSnapshot snapshot)
    {
        var global = _config.GetMonitoring().Alerts;
        var local = host.Alerts;
        var seen = new HashSet<string>();

        // Температура
        if (snapshot.Sensors.Count > 0)
        {
            foreach (var sensor in snapshot.Sensors.Where(s => s.Type == 0))
            {
                var key = "temp:" + sensor.Id;
                seen.Add(key);
                var limit = AlertRules.TemperatureLimit(sensor, global, local, host.IgnoredSensors);
                if (limit == null)
                {
                    health.Alerts.Remove(key);
                    continue;
                }

                Track(host, health, key, "temperature",
                    over: sensor.Value >= limit.Value,
                    back: sensor.Value <= limit.Value - AlertRules.TemperatureHysteresis,
                    alert: $"{sensor.Name} is {sensor.Value:0.#}°C (limit {limit:0.#}°C)",
                    normal: $"{sensor.Name} is back to {sensor.Value:0.#}°C");
            }
        }
        else
        {
            // Датчики не прочитались — не трогаем их тревоги
            seen.UnionWith(health.Alerts.Keys.Where(k => k.StartsWith("temp:")));
        }

        // Свободное место
        if (snapshot.System != null)
        {
            foreach (var disk in snapshot.System.Disks)
            {
                var key = "disk:" + disk.Mount;
                seen.Add(key);
                var limit = AlertRules.DiskFreeLimit(disk, global, local);
                if (limit == null)
                {
                    health.Alerts.Remove(key);
                    continue;
                }

                Track(host, health, key, "diskSpace",
                    over: disk.FreePercent <= limit.Value,
                    back: disk.FreePercent >= limit.Value + AlertRules.DiskHysteresisPercent,
                    alert: $"disk {disk.Mount}: {disk.FreePercent:0.#}% free ({FormatGb(disk.FreeBytes)} of {FormatGb(disk.TotalBytes)}, limit {limit:0.#}%)",
                    normal: $"disk {disk.Mount}: {disk.FreePercent:0.#}% free again");
            }
        }
        else
        {
            seen.UnionWith(health.Alerts.Keys.Where(k => k.StartsWith("disk:")));
        }

        // Износ SSD (SMART Percentage Used)
        var wearLimit = AlertRules.SsdWearLimit(global, local);
        foreach (var disk in health.Disks.Where(d => d.PercentUsed.HasValue))
        {
            var key = "wear:" + (string.IsNullOrEmpty(disk.SerialNumber) ? disk.Id : disk.SerialNumber);
            seen.Add(key);
            if (wearLimit == null)
            {
                health.Alerts.Remove(key);
                continue;
            }

            Track(host, health, key, "ssdWear",
                over: disk.PercentUsed >= wearLimit.Value,
                back: disk.PercentUsed < wearLimit.Value,
                alert: $"SSD {disk.Model} is {disk.PercentUsed}% worn out (limit {wearLimit:0}%)",
                normal: $"SSD {disk.Model} wear is {disk.PercentUsed}%");
        }

        // Датчик / диск пропал — снимаем тревогу молча
        foreach (var gone in health.Alerts.Keys.Where(k => !seen.Contains(k)).ToList())
            health.Alerts.Remove(gone);
    }

    private void Track(Host host, HostHealth health, string key, string type, bool over, bool back, string alert, string normal)
    {
        var active = health.Alerts.ContainsKey(key);
        if (over && !active)
        {
            health.Alerts[key] = alert;
            Raise(new HlcEvent { Type = type, Host = host.Name, Title = $"{host.Name}: {alert}" });
        }
        else if (over)
        {
            health.Alerts[key] = alert; // обновляем текущее значение в тексте
        }
        else if (active && back)
        {
            health.Alerts.Remove(key);
            Raise(new HlcEvent { Type = type, Host = host.Name, Title = $"{host.Name}: {normal}" });
        }
    }

    private static string FormatGb(long bytes) => $"{bytes / 1024.0 / 1024 / 1024:0.#} GB";

    /// <summary>Ресурсы машины; старый агент (без /api/system) — null.</summary>
    private async Task<SystemInfo?> GetSystemAsync(Host host, HttpClient client)
    {
        try
        {
            var response = await client.GetAsync($"{host.BaseUrl}/api/system");
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<SystemInfo>() : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "System resources of {Host} unavailable", host.Name);
            return null;
        }
    }

    /// <summary>
    /// Профили вентиляторов: событие fanProfile, когда профиль ушёл в fail-safe (датчик пропал — вентилятор на 100%)
    /// или потерял вентилятор, и когда снова работает. Короткое sensorMissing (до fail-safe) не сообщается.
    /// </summary>
    private async Task<List<(ProfileInfo, ProfileStatusInfo)>> CheckProfilesAsync(Host host, HostHealth health, HttpClient client)
    {
        var problems = new List<(ProfileInfo, ProfileStatusInfo)>();
        try
        {
            var response = await client.GetAsync($"{host.BaseUrl}/api/profiles/status");
            if (!response.IsSuccessStatusCode)
                return problems; // агент старше 2.0.7

            var statuses = await response.Content.ReadFromJsonAsync<List<ProfileStatusInfo>>() ?? new();
            var profiles = await client.GetFromJsonAsync<List<ProfileInfo>>($"{host.BaseUrl}/api/profiles") ?? new();

            foreach (var status in statuses)
            {
                var profile = profiles.FirstOrDefault(p => p.Id == status.ProfileId)
                              ?? new ProfileInfo { Id = status.ProfileId, Name = status.ProfileId };
                if (status.IsProblem)
                    problems.Add((profile, status));

                var reported = health.ProfileProblems.Contains(status.ProfileId);
                if (status.State is ProfileStatusInfo.Failsafe or ProfileStatusInfo.FanMissing && !reported)
                {
                    health.ProfileProblems.Add(status.ProfileId);
                    Raise(new HlcEvent
                    {
                        Type = "fanProfile", Host = host.Name,
                        Title = status.State == ProfileStatusInfo.Failsafe
                            ? $"{host.Name}: fan profile {profile.Name} lost its sensor"
                            : $"{host.Name}: fan profile {profile.Name} lost its fan",
                        Message = status.State == ProfileStatusInfo.Failsafe
                            ? $"Sensor {status.SensorId} not found — fan {status.FanControllerId} runs at 100% (fail-safe). Pick the sensor again in the profile."
                            : $"Fan {status.FanControllerId} not found — the profile controls nothing."
                    });
                }
                else if (status.State == ProfileStatusInfo.Ok && reported)
                {
                    health.ProfileProblems.Remove(status.ProfileId);
                    Raise(new HlcEvent
                    {
                        Type = "fanProfile", Host = host.Name,
                        Title = $"{host.Name}: fan profile {profile.Name} works again",
                        Message = $"Sensor {status.SensorId}: {status.Temperature:0.#}°C, fan at {status.Speed}%"
                    });
                }
            }

            // Профиль выключили или удалили — проблема снята без уведомления
            health.ProfileProblems.RemoveWhere(id => statuses.All(s => s.ProfileId != id));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fan profile status of {Host} unavailable", host.Name);
        }

        return problems;
    }

    /// <summary>Деградация SMART: диск стал FAILED или выросло число атрибутов в warning/failed.</summary>
    private void CheckSmart(Host host, HostHealth health)
    {
        foreach (var disk in health.Disks)
        {
            var key = string.IsNullOrEmpty(disk.SerialNumber) ? disk.Id : disk.SerialNumber;
            var warnings = disk.Attributes.Count(a => a.Status != AttributeStatus.Ok)
                           + (disk.NvmeHealth?.CriticalWarning > 0 ? 1 : 0);
            var failed = disk.Health == SmartHealthStatus.Failed;

            if (health.DiskState.TryGetValue(key, out var prev))
            {
                if (failed && !prev.Failed)
                    Raise(new HlcEvent
                    {
                        Type = "smart", Host = host.Name, Title = $"{host.Name}: disk {disk.Model} FAILED",
                        Message = $"{disk.Model} ({disk.SerialNumber}, {disk.DevicePath}): SMART overall health FAILED"
                    });
                else if (warnings > prev.Warnings)
                {
                    var bad = string.Join(", ", disk.Attributes.Where(a => a.Status != AttributeStatus.Ok).Select(a => $"{a.Name}={a.RawString}"));
                    Raise(new HlcEvent
                    {
                        Type = "smart", Host = host.Name, Title = $"{host.Name}: disk {disk.Model} degraded",
                        Message = $"{disk.Model} ({disk.SerialNumber}): {warnings} SMART warning(s): {bad}"
                    });
                }
            }

            // Первый скан — только запоминаем состояние (не шлём уведомления о старых проблемах при каждом старте HLC)
            health.DiskState[key] = (failed, warnings);
        }
    }

    private class HostHealth
    {
        public int Failures { get; set; }
        public bool ReportedOffline { get; set; }
        public bool WasSeen { get; set; }
        public DateTime LastSmart { get; set; } = DateTime.MinValue;
        public List<SmartDiskInfo> Disks { get; set; } = new();
        public Dictionary<string, (bool Failed, int Warnings)> DiskState { get; } = new();

        /// <summary>Профили, о проблеме которых уже сообщили (fail-safe / нет вентилятора).</summary>
        public HashSet<string> ProfileProblems { get; } = new();

        /// <summary>Активные тревоги по порогам: ключ → текст.</summary>
        public Dictionary<string, string> Alerts { get; } = new();
    }

    /// <summary>Кольцевой буфер температур хоста.</summary>
    private class HostHistory
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, (string Name, List<(DateTime, double)> Points)> _series = new();

        public void Add(DateTime time, List<SensorInfo> sensors, TimeSpan keep)
            => AddValues(time, sensors.Where(s => s.Type == 0).Select(s => (s.Id, s.Name, s.Value)), keep); // 0 = Temperature

        public void AddValues(DateTime time, IEnumerable<(string Id, string Name, double Value)> values, TimeSpan keep)
        {
            var cutoff = time - keep;
            lock (_lock)
            {
                foreach (var (id, name, value) in values)
                {
                    if (!_series.TryGetValue(id, out var series))
                        _series[id] = series = (name, new List<(DateTime, double)>());

                    series.Points.Add((time, value));
                    var old = series.Points.FindIndex(p => p.Item1 >= cutoff);
                    if (old > 0)
                        series.Points.RemoveRange(0, old);
                }
            }
        }

        public Dictionary<string, (string Name, List<(DateTime Time, double Value)> Points)> Snapshot()
        {
            lock (_lock)
                return _series.ToDictionary(kv => kv.Key, kv => (kv.Value.Name, kv.Value.Points.Select(p => (p.Item1, p.Item2)).ToList()));
        }
    }
}
