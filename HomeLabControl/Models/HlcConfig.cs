namespace HomeLabControl.Models;

/// <summary>
/// Единый конфиг HomeLabControl (config/HomeLabControl.yaml):
///  modules — настройки модулей, hosts — машины с секциями модулей, deploy — пути для деплоя агента.
/// Раньше был разнесён по PowerControl.yaml / FanControl.yaml / BackupManager.yaml — см. HlcConfigService.Migrate.
/// </summary>
public class HlcConfig
{
    public ModulesConfig Modules { get; set; } = new();

    /// <summary>
    /// Все машины. Хост участвует в модуле, если у него есть секция этого модуля
    /// (agent / power / fanControl / smart / backup).
    /// </summary>
    public List<Host> Hosts { get; set; } = new();

    public DeployConfig Deploy { get; set; } = new();

    /// <summary>Старый формат (пути к отдельным файлам). Только для миграции, в новый файл не пишется.</summary>
    public ConfigPaths? ConfigPaths { get; set; }
}

public class ModulesConfig
{
    public PowerControlSettings Power { get; set; } = new();
    public FanControlSettings FanControl { get; set; } = new();
    public BackupModuleConfig Backup { get; set; } = new();
}

public class FanControlSettings
{
    public int ApiTimeoutSeconds { get; set; } = 5;
    public int UpdateIntervalSeconds { get; set; } = 2;
}

public class BackupModuleConfig
{
    public BackupGlobalSettings Settings { get; set; } = new();
    public List<BackupStorage> Storages { get; set; } = new();
    public List<BackupTemplate> Templates { get; set; } = new();
}

public class ConfigPaths
{
    public string? PowerControl { get; set; }
    public string? FanControl { get; set; }
    public string? BackupManager { get; set; }
}

public class DeployConfig
{
    /// <summary>
    /// SSH-ключ HomeLabControl для деплоя/обновления агентов. Создаётся автоматически (ssh-keygen),
    /// публичная часть ставится на хост при первом деплое по паролю. Держите в том же томе, что и конфиг.
    /// </summary>
    public string SshKeyPath { get; set; } = "config/hlc_deploy_ed25519";

    /// <summary>
    /// Пути по ОС. sourcePath нужен только без вшитых в образ сборок агента (/app/agent/&lt;rid&gt;),
    /// например при запуске HLC из исходников.
    /// </summary>
    public Dictionary<string, ModuleDeployConfig> Linux { get; set; } = new();
    public Dictionary<string, ModuleDeployConfig> Windows { get; set; } = new();
}

public class ModuleDeployConfig
{
    public string SourcePath { get; set; } = "";
    public string InstallPath { get; set; } = "";
    public string ExecutableName { get; set; } = "";
}
