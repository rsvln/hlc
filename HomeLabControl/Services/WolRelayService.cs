using System.Collections.Concurrent;
using HomeLabControl.Models;
using Renci.SshNet;
using Host = HomeLabControl.Models.Host;

namespace HomeLabControl.Services;

/// <summary>
/// Wake-on-LAN в другие подсети через ретранслятор (modules.power.wolRelays):
/// ssh — шлюз OpenWrt / Linux запускает etherwake по SSH-ключу HLC; agent — хост с агентом в той сети (POST /api/power/wol).
/// Ретранслятор выбирается по подсети хоста (самая узкая), power.wolVia у хоста — явный выбор или local.
/// </summary>
public class WolRelayService
{
    private readonly HlcConfigService _config;
    private readonly DeployService _deploy;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WolRelayService> _logger;

    /// <summary>Результат последней проверки ретранслятора (для грида).</summary>
    private readonly ConcurrentDictionary<string, (bool Ok, string Message, DateTime Time)> _status = new(StringComparer.OrdinalIgnoreCase);

    public WolRelayService(HlcConfigService config, DeployService deploy, IHttpClientFactory httpClientFactory, ILogger<WolRelayService> logger)
    {
        _config = config;
        _deploy = deploy;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public List<WolRelay> GetRelays() => _config.GetPowerSettings().WolRelays ?? new List<WolRelay>();

    public (bool Ok, string Message, DateTime Time)? GetStatus(string relayName)
        => _status.TryGetValue(relayName, out var s) ? s : null;

    // ─── Выбор ────────────────────────────────────────────────────────────────

    /// <summary>Ретранслятор для хоста; null — broadcast из сети HLC.</summary>
    public WolRelay? Resolve(Host host) => Resolve(host, GetRelays());

    public static WolRelay? Resolve(Host host, IReadOnlyList<WolRelay> relays)
    {
        var via = host.Power?.WolVia;
        if (!string.IsNullOrWhiteSpace(via))
            return via.Equals("local", StringComparison.OrdinalIgnoreCase)
                ? null
                : relays.FirstOrDefault(r => r.Name.Equals(via, StringComparison.OrdinalIgnoreCase));

        // Самая узкая подходящая подсеть; агент не будит сам себя
        return relays
            .Select((r, i) => (Relay: r, Index: i, Ok: IpSubnet.TryParse(r.Subnet, out var subnet), Subnet: subnet))
            .Where(x => x.Ok && x.Subnet.Contains(host.Ip) &&
                        !(x.Relay.Type == WolRelay.Agent && host.Name.Equals(x.Relay.Host, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.Subnet.PrefixLength).ThenBy(x => x.Index)
            .Select(x => x.Relay)
            .FirstOrDefault();
    }

    /// <summary>Хосты с MAC, которые будятся через этот ретранслятор.</summary>
    public List<Host> HostsFor(WolRelay relay)
        => _config.GetHosts().Where(h => !string.IsNullOrWhiteSpace(h.Mac) &&
                                         Resolve(h)?.Name.Equals(relay.Name, StringComparison.OrdinalIgnoreCase) == true).ToList();

    /// <summary>"192.168.12.1" или "ha-dacha" — куда заходит ретранслятор.</summary>
    public string Describe(WolRelay relay)
        => relay.Type == WolRelay.Agent
            ? $"agent {relay.Host}"
            : $"ssh {relay.EffectiveUser}@{SshAddress(relay)}{(relay.EffectivePort != 22 ? $":{relay.EffectivePort}" : "")} · {relay.EffectiveInterface}";

    private string? SshAddress(WolRelay relay)
        => string.IsNullOrWhiteSpace(relay.Host)
            ? relay.Address
            : _config.GetHosts().FirstOrDefault(h => h.Name.Equals(relay.Host, StringComparison.OrdinalIgnoreCase))?.Ip ?? relay.Address;

    // ─── Отправка ─────────────────────────────────────────────────────────────

    /// <summary>MAC в каноничном виде AA:BB:CC:DD:EE:FF (он уходит в команду SSH — только hex); null — не MAC.</summary>
    public static string? NormalizeMac(string? mac)
    {
        var hex = (mac ?? "").Replace(":", "").Replace("-", "").Replace(".", "").Trim();
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit))
            return null;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2).ToUpperInvariant()));
    }

    /// <summary>Команда пробуждения для шлюза: etherwake (OpenWrt, Debian), ether-wake (net-tools), wakeonlan.</summary>
    public static string WakeCommand(string iface, string mac)
        => $"if command -v etherwake >/dev/null 2>&1; then etherwake -i {iface} {mac}; " +
           $"elif command -v ether-wake >/dev/null 2>&1; then ether-wake -i {iface} {mac}; " +
           $"elif command -v wakeonlan >/dev/null 2>&1; then wakeonlan {mac}; " +
           "else echo 'no etherwake / ether-wake / wakeonlan on the relay' >&2; exit 127; fi";

    public async Task<(bool Ok, string Message)> SendAsync(WolRelay relay, string mac)
    {
        var normalized = NormalizeMac(mac);
        if (normalized == null)
            return (false, $"invalid MAC '{mac}'");

        try
        {
            if (relay.Type == WolRelay.Agent)
            {
                var host = _config.GetHosts().FirstOrDefault(h => h.Name.Equals(relay.Host, StringComparison.OrdinalIgnoreCase));
                if (host is not { HasAgent: true })
                    return (false, $"relay {relay.Name}: host {relay.Host} has no agent");

                using var client = _config.CreateAgentClient(_httpClientFactory, host, 10);
                var response = await client.PostAsync($"{host.BaseUrl}/api/power/wol?mac={Uri.EscapeDataString(normalized)}", null);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return (false, $"relay {relay.Name}: agent on {host.Name} is too old for WoL relay — update it");
                return response.IsSuccessStatusCode
                    ? (true, $"via {relay.Name} (agent {host.Name})")
                    : (false, $"relay {relay.Name}: agent answered {(int)response.StatusCode}");
            }

            var address = SshAddress(relay);
            if (string.IsNullOrWhiteSpace(address))
                return (false, $"relay {relay.Name}: no address");

            var log = new DeployResult();
            await Task.Run(() =>
            {
                var connection = _deploy.CreateConnectionInfo(address, relay.EffectivePort, relay.EffectiveUser, null);
                using var ssh = new SshClient(connection);
                ssh.Connect();
                DeployService.Run(ssh, WakeCommand(relay.EffectiveInterface, normalized), log, throwOnError: true);
                ssh.Disconnect();
            });
            return (true, $"via {relay.Name} ({address})");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WoL via relay {Relay} failed", relay.Name);
            return (false, $"relay {relay.Name}: {ex.Message}");
        }
    }

    // ─── Проверка и настройка ─────────────────────────────────────────────────

    /// <summary>agent — агент отвечает и принимает ключ; ssh — вход по ключу HLC и наличие etherwake / ether-wake / wakeonlan.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(WolRelay relay)
    {
        (bool, string) result;
        try
        {
            if (relay.Type == WolRelay.Agent)
            {
                var host = _config.GetHosts().FirstOrDefault(h => h.Name.Equals(relay.Host, StringComparison.OrdinalIgnoreCase));
                if (host is not { HasAgent: true })
                {
                    result = (false, $"host {relay.Host} has no agent");
                }
                else
                {
                    using var client = _config.CreateAgentClient(_httpClientFactory, host, 5);
                    var info = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{host.BaseUrl}/api/agent/info");
                    var version = info.TryGetProperty("version", out var v) ? v.GetString() : "?";
                    var accepted = info.TryGetProperty("keyAccepted", out var k) && k.GetBoolean() ||
                                   info.TryGetProperty("authRequired", out var a) && !a.GetBoolean();
                    result = accepted ? (true, $"agent v{version} answers") : (false, $"agent v{version} rejects the key");
                }
            }
            else
            {
                var address = SshAddress(relay) ?? throw new InvalidOperationException("no address");
                var log = new DeployResult();
                var tool = await Task.Run(() =>
                {
                    var connection = _deploy.CreateConnectionInfo(address, relay.EffectivePort, relay.EffectiveUser, null);
                    using var ssh = new SshClient(connection);
                    ssh.Connect();
                    var found = DeployService.Run(ssh, "command -v etherwake || command -v ether-wake || command -v wakeonlan || true", log).Trim();
                    ssh.Disconnect();
                    return found;
                });
                result = tool.Length > 0
                    ? (true, $"ssh ok, {Path.GetFileName(tool.Split('\n')[0].Trim())}")
                    : (false, "ssh ok, but no etherwake / ether-wake / wakeonlan — run Setup");
            }
        }
        catch (Exception ex)
        {
            result = (false, ex.Message);
        }

        _status[relay.Name] = (result.Item1, result.Item2, DateTime.Now);
        return result;
    }

    /// <summary>
    /// ssh: положить ключ HLC (OpenWrt — /etc/dropbear/authorized_keys, иначе ~/.ssh/authorized_keys) и поставить etherwake,
    /// если его нет (opkg на OpenWrt, apt на Debian / Ubuntu). Пароль нужен, только пока ключа HLC на шлюзе нет.
    /// </summary>
    public async Task<DeployResult> SetupAsync(WolRelay relay, string? password, DeployResult? result = null)
    {
        result ??= new DeployResult();
        try
        {
            if (relay.Type != WolRelay.Ssh)
                throw new InvalidOperationException("Setup is only for ssh relays");
            var address = SshAddress(relay) ?? throw new InvalidOperationException("no address");

            _deploy.EnsureSshKey();
            var publicKey = _deploy.GetPublicKey() ?? throw new InvalidOperationException("HLC SSH key is not available");

            await Task.Run(() =>
            {
                var connection = _deploy.CreateConnectionInfo(address, relay.EffectivePort, relay.EffectiveUser, password);
                using var ssh = new SshClient(connection);
                ssh.Connect();
                result.AddLog($"SSH connected to {relay.EffectiveUser}@{address}");

                var openWrt = DeployService.Run(ssh, "[ -f /etc/openwrt_release ] && echo openwrt || echo other", result).Trim() == "openwrt";
                var keyFile = openWrt ? "/etc/dropbear/authorized_keys" : "~/.ssh/authorized_keys";
                var keyDir = openWrt ? "/etc/dropbear" : "~/.ssh";
                DeployService.Run(ssh,
                    $"mkdir -p {keyDir} && chmod 700 {keyDir} && touch {keyFile} && " +
                    $"(grep -qxF '{publicKey}' {keyFile} || echo '{publicKey}' >> {keyFile}) && chmod 600 {keyFile}",
                    result, throwOnError: true);
                result.AddLog($"HLC SSH key installed in {keyFile}");

                var tool = DeployService.Run(ssh, "command -v etherwake || command -v ether-wake || command -v wakeonlan || true", result).Trim();
                if (tool.Length == 0)
                {
                    if (openWrt)
                        DeployService.Run(ssh, "opkg update >/dev/null && opkg install etherwake", result, throwOnError: true);
                    else if (DeployService.Run(ssh, "command -v apt-get || true", result).Trim().Length > 0)
                        DeployService.Run(ssh, "DEBIAN_FRONTEND=noninteractive apt-get install -y etherwake", result, throwOnError: true);
                    else
                        throw new InvalidOperationException("No etherwake / ether-wake / wakeonlan and no opkg / apt-get — install one of them by hand");
                    result.AddLog("etherwake installed");
                }
                else
                {
                    result.AddLog($"Wake tool found: {tool.Split('\n')[0].Trim()}");
                }

                ssh.Disconnect();
            });

            var (ok, message) = await TestAsync(relay);
            result.Success = ok;
            result.Message = ok ? $"Relay {relay.Name} is ready: {message}" : $"Relay {relay.Name}: {message}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Relay {Relay} setup failed", relay.Name);
            result.Success = false;
            result.Message = ex.Message;
            result.AddLog($"ERROR: {ex.Message}");
        }

        return result;
    }
}
