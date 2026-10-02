using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomeLabControl.Models;
using MQTTnet;
using Host = HomeLabControl.Models.Host;

namespace HomeLabControl.Services;

/// <summary>
/// Мост в MQTT / Home Assistant (modules.mqtt). Для каждого хоста с агентом публикуется устройство (MQTT discovery):
///  - binary_sensor online (connectivity);
///  - sensor на каждый датчик температуры и вентилятор (состояния одним JSON — &lt;base&gt;/&lt;host&gt;/sensors);
///  - binary_sensor problem на каждый диск по SMART (&lt;base&gt;/&lt;host&gt;/smart);
///  - кнопки WoL / shutdown / reboot (если commands: true) — команды из &lt;base&gt;/&lt;host&gt;/cmd/&lt;action&gt;.
/// Доступность HLC — &lt;base&gt;/status (online / offline через LWT).
/// </summary>
public class MqttService : BackgroundService
{
    private static readonly Regex Unsafe = new("[^a-z0-9_]+");

    private readonly HlcConfigService _config;
    private readonly AgentMonitorService _monitor;
    private readonly PowerControlService _power;
    private readonly AuditService _audit;
    private readonly ILogger<MqttService> _logger;

    private readonly MqttClientFactory _factory = new();
    private IMqttClient? _client;
    private string? _connectedSignature;

    // Что уже анонсировано в discovery (host → id сущностей), чтобы переанонсировать только новое
    private readonly ConcurrentDictionary<string, HashSet<string>> _announced = new();

    public MqttService(HlcConfigService config, AgentMonitorService monitor, PowerControlService power, AuditService audit, ILogger<MqttService> logger)
    {
        _config = config;
        _monitor = monitor;
        _power = power;
        _audit = audit;
        _logger = logger;
    }

    public bool IsConnected => _client?.IsConnected == true;

    public static string Slug(string value) => Unsafe.Replace(value.ToLowerInvariant(), "_").Trim('_');

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _monitor.Polled += OnPolled;
        _config.Changed += () => _announced.Clear(); // конфиг поменялся — переанонсируем устройства

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _config.GetMqtt();
            var signature = $"{settings.Enabled}|{settings.Host}|{settings.Port}|{settings.Username}|{settings.Password}|{settings.BaseTopic}|{settings.Commands}";

            try
            {
                if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Host))
                {
                    await DisconnectAsync();
                }
                else if (_client is not { IsConnected: true } || signature != _connectedSignature)
                {
                    await DisconnectAsync();
                    await ConnectAsync(settings, stoppingToken);
                    _connectedSignature = signature;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("MQTT connection to {Host}:{Port} failed: {Error}", settings.Host, settings.Port, ex.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _monitor.Polled -= OnPolled;
        await DisconnectAsync();
    }

    private async Task ConnectAsync(MqttSettings settings, CancellationToken ct)
    {
        var baseTopic = settings.BaseTopic.TrimEnd('/');
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithClientId($"homelabcontrol-{Environment.MachineName}".ToLowerInvariant())
            .WithCleanSession()
            .WithWillTopic($"{baseTopic}/status")
            .WithWillPayload("offline")
            .WithWillRetain()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));

        if (!string.IsNullOrEmpty(settings.Username))
            builder = builder.WithCredentials(settings.Username, settings.Password);

        var client = _factory.CreateMqttClient();
        client.ApplicationMessageReceivedAsync += OnMessageAsync;
        await client.ConnectAsync(builder.Build(), ct);
        _client = client;

        await PublishAsync($"{baseTopic}/status", "online", retain: true);

        if (settings.Commands)
        {
            await client.SubscribeAsync(_factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic($"{baseTopic}/+/cmd/+"))
                .Build(), ct);
        }

        _announced.Clear();
        _logger.LogInformation("MQTT connected to {Host}:{Port}, base topic {Base}", settings.Host, settings.Port, baseTopic);

        // Сразу публикуем то, что уже известно
        foreach (var host in _config.GetHosts().Where(h => h.HasAgent))
        {
            if (_monitor.GetSnapshot(host.Name) is { } snapshot)
                await PublishHostAsync(host, snapshot);
        }
    }

    private async Task DisconnectAsync()
    {
        var client = _client;
        _client = null;
        _connectedSignature = null;
        if (client == null)
            return;

        try
        {
            if (client.IsConnected)
            {
                await PublishAsync(client, $"{_config.GetMqtt().BaseTopic.TrimEnd('/')}/status", "offline", retain: true);
                await client.DisconnectAsync();
            }
        }
        catch
        {
            // соединение уже потеряно
        }
        finally
        {
            client.Dispose();
        }
    }

    // ─── Состояния ────────────────────────────────────────────────────────────

    private void OnPolled(Host host, HostSnapshot snapshot) => _ = PublishHostAsync(host, snapshot);

    private async Task PublishHostAsync(Host host, HostSnapshot snapshot)
    {
        if (_client is not { IsConnected: true })
            return;

        try
        {
            var settings = _config.GetMqtt();
            var baseTopic = settings.BaseTopic.TrimEnd('/');
            var hostSlug = Slug(host.Name);

            await AnnounceAsync(host, snapshot, settings);

            var online = snapshot.Status.State is AgentState.Online or AgentState.Open or AgentState.Legacy or AgentState.KeyRejected;
            await PublishAsync($"{baseTopic}/{hostSlug}/online", online ? "ON" : "OFF", retain: true);

            if (snapshot.Sensors.Count > 0 || snapshot.Fans.Count > 0)
            {
                var values = new Dictionary<string, object>();
                foreach (var s in snapshot.Sensors.Where(s => s.Type == 0))
                    values[Slug(s.Id)] = Math.Round(s.Value, 1);
                foreach (var f in snapshot.Fans)
                    values["fan_" + Slug(f.Id)] = f.CurrentSpeed;
                await PublishAsync($"{baseTopic}/{hostSlug}/sensors", JsonSerializer.Serialize(values), retain: true);
            }

            if (snapshot.System is { } sys)
            {
                var resources = new Dictionary<string, object?>
                {
                    ["cpu"] = sys.CpuPercent,
                    ["memory"] = sys.MemoryPercent
                };
                foreach (var d in sys.Disks)
                    resources["disk_" + Slug(d.Mount)] = d.FreePercent;
                await PublishAsync($"{baseTopic}/{hostSlug}/resources", JsonSerializer.Serialize(resources), retain: true);
            }

            await PublishAsync($"{baseTopic}/{hostSlug}/alerts",
                JsonSerializer.Serialize(new { state = snapshot.Alerts.Count > 0 ? "ON" : "OFF", alerts = snapshot.Alerts.Values }), retain: true);

            if (snapshot.Disks.Count > 0)
            {
                var problems = snapshot.Disks.ToDictionary(
                    d => "disk_" + Slug(string.IsNullOrEmpty(d.SerialNumber) ? d.Id : d.SerialNumber),
                    d => d.Health == SmartHealthStatus.Failed || d.HasWarnings ? "ON" : "OFF");
                await PublishAsync($"{baseTopic}/{hostSlug}/smart", JsonSerializer.Serialize(problems), retain: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MQTT publish for {Host} failed", host.Name);
        }
    }

    /// <summary>MQTT discovery для Home Assistant — только новые сущности.</summary>
    private async Task AnnounceAsync(Host host, HostSnapshot snapshot, MqttSettings settings)
    {
        var dp = settings.DiscoveryPrefix.TrimEnd('/');
        var baseTopic = settings.BaseTopic.TrimEnd('/');
        var hostSlug = Slug(host.Name);
        var announced = _announced.GetOrAdd(host.Name, _ => new HashSet<string>());

        var device = new Dictionary<string, object?>
        {
            ["identifiers"] = new[] { $"hlc_{hostSlug}" },
            ["name"] = host.Name,
            ["manufacturer"] = "HomeLabControl",
            ["model"] = "HomeLabControlAgent " + (host.Agent?.Os ?? ""),
            ["sw_version"] = snapshot.Status.Version
        };

        async Task Entity(string component, string objectId, Dictionary<string, object?> payload)
        {
            if (!announced.Add($"{component}/{objectId}"))
                return;

            payload["unique_id"] = $"hlc_{hostSlug}_{objectId}";
            payload["object_id"] = $"hlc_{hostSlug}_{objectId}";
            payload["device"] = device;
            payload["availability_topic"] = $"{baseTopic}/status";
            await PublishAsync($"{dp}/{component}/hlc_{hostSlug}/{objectId}/config",
                JsonSerializer.Serialize(payload.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value)), retain: true);
        }

        await Entity("binary_sensor", "online", new()
        {
            ["name"] = "Online",
            ["state_topic"] = $"{baseTopic}/{hostSlug}/online",
            ["device_class"] = "connectivity"
        });

        foreach (var s in snapshot.Sensors.Where(s => s.Type == 0))
        {
            var id = Slug(s.Id);
            await Entity("sensor", id, new()
            {
                ["name"] = s.Name,
                ["state_topic"] = $"{baseTopic}/{hostSlug}/sensors",
                ["value_template"] = $"{{{{ value_json['{id}'] }}}}",
                ["unit_of_measurement"] = "°C",
                ["device_class"] = "temperature",
                ["state_class"] = "measurement"
            });
        }

        foreach (var f in snapshot.Fans)
        {
            var id = "fan_" + Slug(f.Id);
            await Entity("sensor", id, new()
            {
                ["name"] = f.Name,
                ["state_topic"] = $"{baseTopic}/{hostSlug}/sensors",
                ["value_template"] = $"{{{{ value_json['{id}'] }}}}",
                ["unit_of_measurement"] = "RPM",
                ["state_class"] = "measurement",
                ["icon"] = "mdi:fan"
            });
        }

        foreach (var d in snapshot.Disks)
        {
            var id = "disk_" + Slug(string.IsNullOrEmpty(d.SerialNumber) ? d.Id : d.SerialNumber);
            await Entity("binary_sensor", id, new()
            {
                ["name"] = $"Disk {d.Model} {d.MountPointsLabel}".Trim(),
                ["state_topic"] = $"{baseTopic}/{hostSlug}/smart",
                ["value_template"] = $"{{{{ value_json['{id}'] }}}}",
                ["device_class"] = "problem"
            });
        }

        if (snapshot.System is { } sys)
        {
            await Entity("sensor", "cpu", new()
            {
                ["name"] = "CPU", ["state_topic"] = $"{baseTopic}/{hostSlug}/resources",
                ["value_template"] = "{{ value_json['cpu'] }}", ["unit_of_measurement"] = "%",
                ["state_class"] = "measurement", ["icon"] = "mdi:cpu-64-bit"
            });
            await Entity("sensor", "memory", new()
            {
                ["name"] = "Memory", ["state_topic"] = $"{baseTopic}/{hostSlug}/resources",
                ["value_template"] = "{{ value_json['memory'] }}", ["unit_of_measurement"] = "%",
                ["state_class"] = "measurement", ["icon"] = "mdi:memory"
            });
            foreach (var d in sys.Disks)
            {
                var id = "disk_" + Slug(d.Mount);
                await Entity("sensor", id, new()
                {
                    ["name"] = $"Free {d.Mount}", ["state_topic"] = $"{baseTopic}/{hostSlug}/resources",
                    ["value_template"] = $"{{{{ value_json['{id}'] }}}}", ["unit_of_measurement"] = "%",
                    ["state_class"] = "measurement", ["icon"] = "mdi:harddisk"
                });
            }
        }

        await Entity("binary_sensor", "alerts", new()
        {
            ["name"] = "Alerts",
            ["state_topic"] = $"{baseTopic}/{hostSlug}/alerts",
            ["value_template"] = "{{ value_json.state }}",
            ["json_attributes_topic"] = $"{baseTopic}/{hostSlug}/alerts",
            ["device_class"] = "problem"
        });

        if (settings.Commands && host.Power != null)
        {
            if (!string.IsNullOrWhiteSpace(host.Mac))
                await Entity("button", "wol", new() { ["name"] = "Wake on LAN", ["command_topic"] = $"{baseTopic}/{hostSlug}/cmd/wol", ["icon"] = "mdi:power" });
            await Entity("button", "shutdown", new() { ["name"] = "Shutdown", ["command_topic"] = $"{baseTopic}/{hostSlug}/cmd/shutdown", ["icon"] = "mdi:power-off" });
            await Entity("button", "reboot", new() { ["name"] = "Reboot", ["command_topic"] = $"{baseTopic}/{hostSlug}/cmd/reboot", ["device_class"] = "restart" });
        }
    }

    // ─── Команды ──────────────────────────────────────────────────────────────

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var settings = _config.GetMqtt();
        if (!settings.Commands)
            return;

        // <base>/<host>/cmd/<action>
        var parts = e.ApplicationMessage.Topic.Split('/');
        if (parts.Length < 3 || parts[^2] != "cmd")
            return;

        var hostSlug = parts[^3];
        var action = parts[^1];
        var host = _config.GetHosts().FirstOrDefault(h => Slug(h.Name) == hostSlug && h.Power != null);
        if (host == null)
        {
            _logger.LogWarning("MQTT command for unknown host {Host}", hostSlug);
            return;
        }

        var delay = host.DefaultDelaySeconds ?? _config.GetPowerSettings().DefaultDelaySeconds;
        var ok = action switch
        {
            "wol" when !string.IsNullOrWhiteSpace(host.Mac) => await _power.WakeOnLanAsync(host.Mac!),
            "shutdown" when host.HasAgent => await _power.ShutdownAsync(host.Ip, host.Port, delay, false),
            "reboot" when host.HasAgent => await _power.RebootAsync(host.Ip, host.Port, delay, false),
            _ => false
        };

        _audit.Log("mqtt", $"power.{action}", host.Name, "mqtt command", ok);
    }

    // ─── Публикация ───────────────────────────────────────────────────────────

    public Task PublishEventAsync(HlcEvent e)
    {
        var baseTopic = _config.GetMqtt().BaseTopic.TrimEnd('/');
        return PublishAsync($"{baseTopic}/events", JsonSerializer.Serialize(new
        {
            time = e.Time,
            type = e.Type,
            host = e.Host,
            title = e.Title,
            message = e.Message
        }), retain: false);
    }

    private Task PublishAsync(string topic, string payload, bool retain)
        => _client is { IsConnected: true } client ? PublishAsync(client, topic, payload, retain) : Task.CompletedTask;

    private static Task PublishAsync(IMqttClient client, string topic, string payload, bool retain)
        => client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithRetainFlag(retain)
            .Build());
}
