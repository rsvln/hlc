namespace HomeLabControl.Models
{
    /// <summary>
    /// Пороги уведомлений по ресурсам (modules.monitoring.alerts). 0 — проверка выключена.
    /// Температура — по классу железа (cpu / gpu / storage, класс берётся из id датчика);
    /// заданные классы дополняют значения по умолчанию, а не заменяют их.
    /// </summary>
    public class AlertSettings
    {
        /// <summary>Свободного места на диске меньше стольких процентов — уведомление.</summary>
        public double DiskFreePercent { get; set; } = 10;

        /// <summary>Износ SSD по SMART (Percentage Used) от стольких процентов.</summary>
        public double SsdWearPercent { get; set; } = 90;

        /// <summary>Пороги температуры по классу: cpu, gpu, storage. Не заданные берутся из <see cref="DefaultTemperature"/>.</summary>
        public Dictionary<string, double>? Temperature { get; set; }

        public static readonly IReadOnlyDictionary<string, double> DefaultTemperature = new Dictionary<string, double>
        {
            ["cpu"] = 90,
            ["gpu"] = 85,
            ["storage"] = 55
        };
    }

    /// <summary>Переопределения порогов у хоста (hosts[].alerts).</summary>
    public class HostAlerts
    {
        public double? DiskFreePercent { get; set; }
        public double? SsdWearPercent { get; set; }

        /// <summary>Ключ — класс (cpu / gpu / storage), id датчика, имя датчика или короткое имя; значение — порог, 0 — не проверять.</summary>
        public Dictionary<string, double>? Temperature { get; set; }

        /// <summary>Датчики без проверки: id, имя или короткое имя (как fanControl.ignoredSensors).</summary>
        public List<string>? IgnoreSensors { get; set; }

        /// <summary>Диски без проверки места: точка монтирования ("/boot/efi", "E:\").</summary>
        public List<string>? IgnoreDisks { get; set; }
    }
}
