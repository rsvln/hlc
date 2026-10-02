using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace HomeLabControl.Models
{
    /// <summary>
    /// Машина домашней лаборатории. Общие поля — name/ip/mac, остальное — секции модулей:
    /// нет секции — хост в модуле не участвует.
    /// </summary>
    public class Host
    {
        public string Name { get; set; } = string.Empty;
        public string Ip { get; set; } = string.Empty;

        /// <summary>MAC для Wake-on-LAN; пусто — WoL недоступен.</summary>
        public string? Mac { get; set; }

        /// <summary>HomeLabControlAgent на хосте (нужен для Power shutdown/reboot, Fan Control, SMART).</summary>
        public AgentSection? Agent { get; set; }

        /// <summary>Power Control: WoL / ping / shutdown / reboot.</summary>
        public PowerSection? Power { get; set; }

        /// <summary>Fan Control: датчики, вентиляторы, профили (через агент).</summary>
        public FanControlSection? FanControl { get; set; }

        /// <summary>SMART Monitor (через агент).</summary>
        public SmartSection? Smart { get; set; }

        /// <summary>Backup Manager: бэкап по SSH (роутеры и т.п.).</summary>
        public BackupSection? Backup { get; set; }

        /// <summary>Переопределения порогов уведомлений (modules.monitoring.alerts) для этого хоста.</summary>
        public HostAlerts? Alerts { get; set; }

        // ─── Вычисляемое и runtime-состояние, в YAML не сохраняется ───────────

        [YamlIgnore] public bool HasAgent => Agent is { Port: > 0 };
        [YamlIgnore] public int Port => Agent?.Port ?? 0;
        [YamlIgnore] public string BaseUrl => $"http://{Ip}:{Port}";
        [YamlIgnore] public int? DefaultDelaySeconds => Power?.DefaultDelaySeconds;
        [YamlIgnore] public List<string> IgnoredSensors => FanControl?.IgnoredSensors ?? new();

        [YamlIgnore] public bool IsOnline { get; set; }
        [YamlIgnore] public int? CountdownSeconds { get; set; }
    }

    public class AgentSection
    {
        public int Port { get; set; } = 8117;

        /// <summary>linux / windows — заполняется деплоем.</summary>
        public string? Os { get; set; }

        /// <summary>Каталог установки и имя службы — заполняются деплоем, нужны для Update/Uninstall.</summary>
        public string? InstallPath { get; set; }
        public string? ServiceName { get; set; }

        /// <summary>SSH для деплоя/обновления (вход по SSH-ключу HomeLabControl, см. deploy.sshKeyPath).</summary>
        public string? SshUser { get; set; }
        public int? SshPort { get; set; }

        /// <summary>
        /// Ключ API агента — один на хост: с ним ходят HLC и Home Assistant (заголовок X-Api-Key в rest_command).
        /// Генерируется деплоем / install-скриптом.
        /// </summary>
        [JsonIgnore] // не отдаём ключи через REST API HomeLabControl
        public string? ApiKey { get; set; }

        /// <summary>Устарело (был отдельный ключ для HA): читается из старых конфигов и не сохраняется.</summary>
        [JsonIgnore]
        public string? HaApiKey { get; set; }
    }

    public class PowerSection
    {
        /// <summary>Задержка shutdown/reboot по умолчанию; не задана — modules.power.defaultDelaySeconds.</summary>
        public int? DefaultDelaySeconds { get; set; }
    }

    public class FanControlSection
    {
        /// <summary>Скрываемые датчики: id, "device/sensor" или короткое имя.</summary>
        public List<string>? IgnoredSensors { get; set; }
    }

    public class SmartSection
    {
    }

    public class BackupSection
    {
        public int SshPort { get; set; } = 22;
        public string User { get; set; } = "root";

        /// <summary>PUSH: ключ BackupManager → хост; PULL: ключ Storage → хост.</summary>
        public string SshKey { get; set; } = "";

        /// <summary>push или pull.</summary>
        public string Direction { get; set; } = "push";

        public string Template { get; set; } = "";
        public string Storage { get; set; } = "";
        public string Schedule { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }
}
