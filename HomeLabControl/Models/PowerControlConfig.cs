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
    }
}