using YamlDotNet.Serialization;

namespace HomeLabControl.Models
{
    /// <summary>Автоматизация питания (modules.automation): задачи по расписанию из последовательных шагов.</summary>
    public class AutomationSettings
    {
        public List<AutomationTask> Tasks { get; set; } = new();
    }

    public class AutomationTask
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }

        /// <summary>Cron из 5 полей, как backup.schedule; пусто — только ручной запуск.</summary>
        public string? Schedule { get; set; }

        public bool Enabled { get; set; } = true;

        public List<AutomationStep> Steps { get; set; } = new();
    }

    /// <summary>
    /// Шаг задачи — ровно одно из: wake, waitOnline, backup, shutdown, reboot (списки хостов / групп) или delay (секунды).
    /// Ошибка шага останавливает задачу; шаги с always: true выполняются и после ошибки (например, выключение).
    /// </summary>
    public class AutomationStep
    {
        /// <summary>Wake-on-LAN и ожидание, пока хосты не появятся в сети.</summary>
        public List<string>? Wake { get; set; }

        /// <summary>Ждать, пока хосты в сети (агент отвечает, без агента — ping).</summary>
        public List<string>? WaitOnline { get; set; }

        /// <summary>Бэкапы хостов (Backup Manager), шаг ждёт их окончания.</summary>
        public List<string>? Backup { get; set; }

        /// <summary>Выключить. Только хосты, которые разбудила эта же задача, если не force: true.</summary>
        public List<string>? Shutdown { get; set; }

        /// <summary>Перезагрузить. Как shutdown — только разбуженные задачей хосты, если не force: true.</summary>
        public List<string>? Reboot { get; set; }

        /// <summary>Пауза, секунд.</summary>
        public int? Delay { get; set; }

        /// <summary>wake / waitOnline: сколько ждать хосты, минут (по умолчанию 10).</summary>
        public int? TimeoutMinutes { get; set; }

        /// <summary>shutdown / reboot: задержка на агенте, секунд (по умолчанию 0).</summary>
        public int? DelaySeconds { get; set; }

        /// <summary>shutdown / reboot: и хосты, включённые до задачи.</summary>
        public bool? Force { get; set; }

        /// <summary>Выполнить, даже если предыдущий шаг упал.</summary>
        public bool? Always { get; set; }

        [YamlIgnore]
        public string? Kind =>
            Wake != null ? "wake" :
            WaitOnline != null ? "waitOnline" :
            Backup != null ? "backup" :
            Shutdown != null ? "shutdown" :
            Reboot != null ? "reboot" :
            Delay != null ? "delay" : null;

        [YamlIgnore]
        public List<string> Targets => Wake ?? WaitOnline ?? Backup ?? Shutdown ?? Reboot ?? new List<string>();

        /// <summary>Сколько типов задано (должен быть ровно один).</summary>
        [YamlIgnore]
        public int KindCount => new object?[] { Wake, WaitOnline, Backup, Shutdown, Reboot, Delay }.Count(x => x != null);

        public override string ToString() => Kind == "delay"
            ? $"delay {Delay}s"
            : $"{Kind} {string.Join(", ", Targets)}{(Force == true ? " (force)" : "")}{(Always == true ? " [always]" : "")}";
    }

    /// <summary>Запуск задачи (история — config/automation-history.json).</summary>
    public class AutomationRun
    {
        public string TaskName { get; set; } = string.Empty;

        /// <summary>schedule / manual / mqtt.</summary>
        public string Trigger { get; set; } = "manual";

        public string? User { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }

        /// <summary>Running / Success / Failed / Skipped.</summary>
        public string Status { get; set; } = "Running";

        public string Log { get; set; } = string.Empty;
    }
}
