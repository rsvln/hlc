using System.Text.RegularExpressions;
using HomeLabControl.Models;

namespace HomeLabControl.Services;

/// <summary>Проверка конфига перед сохранением (YAML-редактор и правки из UI): автоматизация и ретрансляторы WoL.</summary>
public static class ConfigValidator
{
    private static readonly Regex SafeAddress = new(@"^[A-Za-z0-9.\-]+$");
    private static readonly Regex SafeToken = new(@"^[A-Za-z0-9_.@\-]+$");

    public static List<string> Validate(HlcConfig config)
    {
        var errors = AutomationService.Validate(config);
        errors.AddRange(ValidateWolRelays(config));
        return errors;
    }

    public static List<string> ValidateWolRelays(HlcConfig config)
    {
        var errors = new List<string>();
        var relays = config.Modules.Power.WolRelays ?? new List<WolRelay>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in relays)
        {
            var label = string.IsNullOrWhiteSpace(r.Name) ? "(no name)" : r.Name;
            if (string.IsNullOrWhiteSpace(r.Name))
                errors.Add("wolRelays: a relay without a name");
            else if (!names.Add(r.Name) || r.Name.Equals("local", StringComparison.OrdinalIgnoreCase))
                errors.Add($"wolRelays: duplicate or reserved name '{r.Name}'");

            if (!IpSubnet.TryParse(r.Subnet, out _))
                errors.Add($"wolRelay '{label}': invalid subnet '{r.Subnet}' (e.g. 192.168.12.0/24)");

            var host = string.IsNullOrWhiteSpace(r.Host) ? null
                : config.Hosts.FirstOrDefault(h => h.Name.Equals(r.Host, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(r.Host) && host == null)
                errors.Add($"wolRelay '{label}': unknown host '{r.Host}'");

            switch (r.Type)
            {
                case WolRelay.Agent:
                    if (host == null)
                        errors.Add($"wolRelay '{label}': type agent needs host (a host with an agent)");
                    else if (!host.HasAgent)
                        errors.Add($"wolRelay '{label}': host '{host.Name}' has no agent");
                    break;

                case WolRelay.Ssh:
                    // Значения уходят в команды SSH — только безопасные символы
                    var address = host?.Ip ?? r.Address;
                    if (string.IsNullOrWhiteSpace(address))
                        errors.Add($"wolRelay '{label}': type ssh needs address or host");
                    else if (!SafeAddress.IsMatch(address))
                        errors.Add($"wolRelay '{label}': invalid address '{address}'");
                    if (!SafeToken.IsMatch(r.EffectiveUser))
                        errors.Add($"wolRelay '{label}': invalid sshUser");
                    if (!SafeToken.IsMatch(r.EffectiveInterface))
                        errors.Add($"wolRelay '{label}': invalid interface");
                    if (r.EffectivePort is < 1 or > 65535)
                        errors.Add($"wolRelay '{label}': invalid sshPort");
                    break;

                default:
                    errors.Add($"wolRelay '{label}': type must be ssh or agent");
                    break;
            }
        }

        foreach (var h in config.Hosts.Where(h => !string.IsNullOrWhiteSpace(h.Power?.WolVia)))
        {
            var via = h.Power!.WolVia!;
            if (!via.Equals("local", StringComparison.OrdinalIgnoreCase) &&
                !relays.Any(r => r.Name.Equals(via, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"host '{h.Name}': power.wolVia '{via}' is not a relay name (or 'local')");
        }

        return errors;
    }
}
