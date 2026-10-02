namespace HomeLabControl.Models
{
    /// <summary>Настройки модуля Power Control (modules.power).</summary>
    public class PowerControlSettings
    {
        public int PingIntervalSeconds { get; set; } = 5;
        public int PingTimeoutMs { get; set; } = 1000;
        public int ApiTimeoutSeconds { get; set; } = 2;
        public int WolPort { get; set; } = 9;
        public string BroadcastAddress { get; set; } = "255.255.255.255";
        public int DefaultDelaySeconds { get; set; } = 1;

        /// <summary>Группы хостов: имя → список хостов. Кнопки «всей группе» в Power Control, цели шагов автоматизации.</summary>
        public Dictionary<string, List<string>>? Groups { get; set; }

        /// <summary>Ретрансляторы Wake-on-LAN для хостов в других подсетях (broadcast не проходит маршрутизатор / VPN).</summary>
        public List<WolRelay>? WolRelays { get; set; }
    }

    /// <summary>
    /// Ретранслятор WoL: хосты из Subnet будятся через него.
    /// ssh — шлюз (OpenWrt / Linux) по SSH-ключу HLC запускает etherwake; agent — хост с агентом в той сети (POST /api/power/wol).
    /// </summary>
    public class WolRelay
    {
        public const string Ssh = "ssh";
        public const string Agent = "agent";

        public string Name { get; set; } = string.Empty;

        /// <summary>CIDR: "192.168.12.0/24".</summary>
        public string Subnet { get; set; } = string.Empty;

        /// <summary>ssh или agent.</summary>
        public string Type { get; set; } = Ssh;

        /// <summary>agent: хост с агентом; ssh: хост, на который заходить (вместо Address).</summary>
        public string? Host { get; set; }

        /// <summary>ssh: адрес шлюза.</summary>
        public string? Address { get; set; }

        public string? SshUser { get; set; }
        public int? SshPort { get; set; }

        /// <summary>ssh: интерфейс локальной сети для etherwake -i (OpenWrt — br-lan).</summary>
        public string? Interface { get; set; }

        [YamlDotNet.Serialization.YamlIgnore] public string EffectiveUser => string.IsNullOrWhiteSpace(SshUser) ? "root" : SshUser!;
        [YamlDotNet.Serialization.YamlIgnore] public int EffectivePort => SshPort ?? 22;
        [YamlDotNet.Serialization.YamlIgnore] public string EffectiveInterface => string.IsNullOrWhiteSpace(Interface) ? "br-lan" : Interface!;
    }
}