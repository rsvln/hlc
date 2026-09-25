namespace HomeLabControl.Models
{
    /// <summary>
    /// Представление Backup Manager: настройки модуля + хосты с секцией backup.
    /// Также — формат старого BackupManager.yaml (для миграции).
    /// </summary>
    public class BackupManagerConfig
    {
        public BackupGlobalSettings Settings { get; set; } = new();
        public List<BackupStorage> Storages { get; set; } = new();
        public List<BackupTemplate> Templates { get; set; } = new();
        public List<BackupHost> Hosts { get; set; } = new();
    }

    public class BackupGlobalSettings
    {
        public string SshKeysPath { get; set; } = "/root/.ssh";
        public int DefaultPort { get; set; } = 22;
        public string DefaultUser { get; set; } = "root";
        public int TimeoutSeconds { get; set; } = 30;
        public int MaxParallelBackups { get; set; } = 3;
        public string LogPath { get; set; } = "/var/log/backup-manager.log";
        public int RetentionDays { get; set; } = 14;
    }

    public class BackupStorage
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "local"; // local, ssh

        // Для local
        public string LocalPath { get; set; } = "";

        // Для ssh
        public string SshHost { get; set; } = "";
        public int SshPort { get; set; } = 22;
        public string SshUser { get; set; } = "";
        public string SshKeyPath { get; set; } = ""; // Ключ для BackupManager → Storage
        public string RemotePath { get; set; } = "";
    }

    public class BackupTemplate
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> PushCommands { get; set; } = new(); // Команды для PUSH режима
        public List<string> PullCommands { get; set; } = new(); // Команды для PULL режима
        public int? RetentionDays { get; set; }
    }

    /// <summary>Хост бэкапа — проекция Host + BackupSection (и формат старого BackupManager.yaml).</summary>
    public class BackupHost
    {
        public string Name { get; set; } = "";
        public string Ip { get; set; } = "";
        public int Port { get; set; } = 22;
        public string User { get; set; } = "root";
        public string SshKey { get; set; } = ""; // Для PUSH: BackupManager→Router, для PULL: Storage→Router
        public string BackupDirection { get; set; } = "push"; // push или pull
        public string Template { get; set; } = "";
        public string Storage { get; set; } = "";
        public string Schedule { get; set; } = "";
        public bool Enabled { get; set; } = true;
        [YamlDotNet.Serialization.YamlIgnore]
        public DateTime? LastBackup { get; set; }

        [YamlDotNet.Serialization.YamlIgnore]
        public string LastStatus { get; set; } = "Never";
    }

    public class BackupJob
    {
        public string HostName { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string Status { get; set; } = "Running";

        /// <summary>manual — кнопка в UI / API, schedule — по расписанию.</summary>
        public string Trigger { get; set; } = "manual";
        public string Log { get; set; } = "";
        public long? SizeBytes { get; set; }
    }

    public class SshKeyInfo
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public bool HasPublicKey { get; set; }
        public bool IsUsed { get; set; }
        public List<string> UsedBy { get; set; } = new();
    }

    public class DeployKeysRequest
    {
        public string HostPassword { get; set; } = "";
        public string StoragePassword { get; set; } = "";
    }
}