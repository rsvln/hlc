using System.Security.Cryptography;
using HomeLabControl.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using HostModel = HomeLabControl.Models.Host;

namespace HomeLabControl.Services;

/// <summary>
/// Единый конфиг config/HomeLabControl.yaml: хосты, настройки, бэкапы, деплой.
/// Файл перечитывается автоматически при изменении на диске.
/// </summary>
public class HlcConfigService : IDisposable
{
    private readonly string _configPath;
    private readonly ILogger<HlcConfigService> _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private HlcConfig _config = new();
    private FileSystemWatcher? _watcher;
    private DateTime _ignoreWatcherUntil = DateTime.MinValue;

    /// <summary>Конфиг перечитан (с диска, из редактора или после деплоя).</summary>
    public event Action? Changed;

    public HlcConfigService(IConfiguration configuration, ILogger<HlcConfigService> logger)
    {
        _logger = logger;
        _configPath = (configuration["AppConfigPath"] ?? "config/HomeLabControl.yaml").Trim();

        Load();
        SetupFileWatcher();
    }

    public string ConfigPath => _configPath;

    // ─── Чтение ───────────────────────────────────────────────────────────────

    public HlcConfig GetHlcConfig()
    {
        lock (_lock) { return _config; }
    }

    public List<HostModel> GetHosts() => GetHlcConfig().Hosts;

    /// <summary>Хосты с секцией power.</summary>
    public List<HostModel> GetPowerHosts() => GetHosts().Where(h => h.Power != null).ToList();

    /// <summary>Группы хостов (modules.power.groups).</summary>
    public Dictionary<string, List<string>> GetGroups() => GetHlcConfig().Modules.Power.Groups ?? new();

    /// <summary>
    /// Имена хостов и групп → хосты (без повторов, в порядке перечисления).
    /// unknown — имена, которых нет ни среди хостов, ни среди групп.
    /// </summary>
    public List<HostModel> ResolveTargets(IEnumerable<string> names, out List<string> unknown)
        => ResolveTargets(GetHlcConfig(), names, out unknown);

    /// <summary>То же для произвольного конфига (проверка перед сохранением).</summary>
    public static List<HostModel> ResolveTargets(HlcConfig config, IEnumerable<string> names, out List<string> unknown)
    {
        var hosts = config.Hosts;
        var groups = config.Modules.Power.Groups ?? new();
        var result = new List<HostModel>();
        unknown = new List<string>();

        void Add(HostModel host)
        {
            if (!result.Contains(host))
                result.Add(host);
        }

        foreach (var name in names)
        {
            var host = hosts.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (host != null)
            {
                Add(host);
                continue;
            }

            var group = groups.FirstOrDefault(g => g.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (group.Value == null)
            {
                unknown.Add(name);
                continue;
            }

            foreach (var member in group.Value)
            {
                var memberHost = hosts.FirstOrDefault(h => h.Name.Equals(member, StringComparison.OrdinalIgnoreCase));
                if (memberHost != null)
                    Add(memberHost);
                else
                    unknown.Add($"{member} (in group {group.Key})");
            }
        }

        return result;
    }

    /// <summary>Хосты с секцией fanControl и агентом.</summary>
    public List<HostModel> GetFanControlHosts() => GetHosts().Where(h => h.FanControl != null && h.HasAgent).ToList();

    /// <summary>Хосты с секцией smart и агентом.</summary>
    public List<HostModel> GetSmartHosts() => GetHosts().Where(h => h.Smart != null && h.HasAgent).ToList();

    /// <summary>Хосты с секцией backup.</summary>
    public List<HostModel> GetBackupHosts() => GetHosts().Where(h => h.Backup != null).ToList();

    public HostModel? FindHost(string ip, int port)
        => GetHosts().FirstOrDefault(h => h.Ip == ip && h.Port == port);

    public PowerControlSettings GetPowerSettings() => GetHlcConfig().Modules.Power;

    public FanControlSettings GetFanControlSettings() => GetHlcConfig().Modules.FanControl;

    public BackupModuleConfig GetBackupModule() => GetHlcConfig().Modules.Backup;

    public MonitoringSettings GetMonitoring() => GetHlcConfig().Modules.Monitoring;

    public NotificationSettings GetNotifications() => GetHlcConfig().Modules.Notifications;

    public MqttSettings GetMqtt() => GetHlcConfig().Modules.Mqtt;

    // ─── Редактор ─────────────────────────────────────────────────────────────

    public string ReadRawYaml()
        => File.Exists(_configPath) ? File.ReadAllText(_configPath) : Serialize(new HlcConfig());

    /// <summary>Проверяет YAML и сохраняет как есть (с комментариями). Кидает исключение при ошибке разбора.</summary>
    public async Task SaveRawYamlAsync(string yaml)
    {
        var parsed = Deserialize(yaml); // ошибка — исключение, файл не трогаем

        var errors = AutomationService.Validate(parsed);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join("\n", errors));

        await _saveLock.WaitAsync();
        try
        {
            await WriteFileAsync(yaml);
            SetConfig(parsed);
        }
        finally
        {
            _saveLock.Release();
        }

        _logger.LogInformation("Config saved from editor: {Hosts} hosts", parsed.Hosts.Count);
    }

    public void Reload()
    {
        Load();
    }

    // ─── Ключи агентов ────────────────────────────────────────────────────────

    /// <summary>
    /// Изменить конфиг программно (деплой, удаление агента) и сохранить.
    /// Сохранение сериализует весь файл — комментарии в YAML при этом теряются.
    /// </summary>
    public async Task UpdateAsync(Action<HlcConfig> mutate)
    {
        await _saveLock.WaitAsync();
        try
        {
            // Работаем с копией, чтобы при ошибке записи не оставить в памяти несохранённое состояние
            var current = GetHlcConfig();
            var config = Deserialize(Serialize(current));
            mutate(config);

            await WriteFileAsync(PatchedText(current, config));
            SetConfig(config);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    // ─── Запись с сохранением комментариев ────────────────────────────────────

    /// <summary>
    /// Текст файла после изменения current → updated: в существующем YAML переписываются только изменившиеся
    /// блоки (modules.X.Y, хосты по имени и их секции, прочие ключи верхнего уровня) — комментарии и порядок
    /// остальных ключей сохраняются. Если результат разбирается не в updated, файл пишется целиком (с .bak).
    /// </summary>
    private string PatchedText(HlcConfig current, HlcConfig updated)
    {
        var full = Serialize(updated);
        if (!File.Exists(_configPath))
            return full;

        var original = File.ReadAllText(_configPath);
        try
        {
            var text = PatchConfigText(original, current, updated);
            if (Serialize(Deserialize(text)) == full)
                return text;
            _logger.LogWarning("Config patch did not reproduce the new config — writing the whole file");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Config patch failed — writing the whole file");
        }

        try
        {
            File.Copy(_configPath, _configPath + ".bak", overwrite: true);
            _logger.LogWarning("Comments in {Path} are lost; the previous file is saved as {Bak}", _configPath, _configPath + ".bak");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to back up {Path}", _configPath);
        }
        return full;
    }

    internal static string PatchConfigText(string text, HlcConfig current, HlcConfig updated)
    {
        foreach (var prop in YamlProperties(typeof(HlcConfig)))
        {
            var key = YamlName(prop);
            var oldValue = prop.GetValue(current);
            var newValue = prop.GetValue(updated);
            if (SerializeValue(oldValue) == SerializeValue(newValue))
                continue;

            if (prop.Name == nameof(HlcConfig.Hosts))
                text = PatchHosts(text, key, current.Hosts, updated.Hosts);
            else if (newValue != null && oldValue != null && prop.PropertyType == typeof(ModulesConfig))
                text = PatchObject(text, new[] { new YamlTextPatcher.Segment(key) }, oldValue, newValue, depth: 2);
            else
                text = YamlTextPatcher.Set(text, new[] { new YamlTextPatcher.Segment(key) }, SerializeValue(newValue));
        }
        return text;
    }

    /// <summary>Свойства объекта по одному; depth > 1 — вложенные объекты разбираются глубже.</summary>
    private static string PatchObject(string text, YamlTextPatcher.Segment[] path, object oldObj, object newObj, int depth)
    {
        foreach (var prop in YamlProperties(oldObj.GetType()))
        {
            var oldValue = prop.GetValue(oldObj);
            var newValue = prop.GetValue(newObj);
            if (SerializeValue(oldValue) == SerializeValue(newValue))
                continue;

            var childPath = path.Append(new YamlTextPatcher.Segment(YamlName(prop))).ToArray();
            // Объект стал пустым ({}) — пишем его целиком, иначе останется "key:" без значения (= null)
            if (depth > 1 && oldValue != null && newValue != null && IsPlainObject(prop.PropertyType) &&
                SerializeValue(newValue)?.Trim() != "{}")
                text = PatchObject(text, childPath, oldValue, newValue, depth - 1);
            else
                text = YamlTextPatcher.Set(text, childPath, SerializeValue(newValue));
        }
        return text;
    }

    private static string PatchHosts(string text, string key, List<HostModel> oldHosts, List<HostModel> newHosts)
    {
        // Порядок хостов поменялся (или дубликаты имён) — проще переписать список целиком
        var oldNames = oldHosts.Select(h => h.Name).ToList();
        var newNames = newHosts.Select(h => h.Name).ToList();
        if (oldNames.Distinct().Count() != oldNames.Count || newNames.Distinct().Count() != newNames.Count ||
            !oldNames.Where(newNames.Contains).SequenceEqual(newNames.Where(oldNames.Contains)))
            return YamlTextPatcher.Set(text, new[] { new YamlTextPatcher.Segment(key) }, SerializeValue(newHosts));

        foreach (var removed in oldHosts.Where(h => !newNames.Contains(h.Name)))
            text = YamlTextPatcher.Set(text, new[] { new YamlTextPatcher.Segment(key, removed.Name) }, null);

        foreach (var host in newHosts)
        {
            var old = oldHosts.FirstOrDefault(h => h.Name == host.Name);
            if (old == null)
                text = YamlTextPatcher.Set(text, new[] { new YamlTextPatcher.Segment(key, host.Name) }, SerializeValue(host));
            else if (SerializeValue(old) != SerializeValue(host))
                text = PatchObject(text, new[] { new YamlTextPatcher.Segment(key, host.Name) }, old, host, depth: 2);
        }
        return text;
    }

    private static IEnumerable<System.Reflection.PropertyInfo> YamlProperties(Type type)
        => type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 &&
                        !p.IsDefined(typeof(YamlDotNet.Serialization.YamlIgnoreAttribute), true));

    private static string YamlName(System.Reflection.PropertyInfo prop) => CamelCaseNamingConvention.Instance.Apply(prop.Name);

    private static bool IsPlainObject(Type type)
        => type.IsClass && type != typeof(string) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type);

    /// <summary>YAML значения для патча; null — ключ удаляется (null и пустые коллекции не пишутся, как при полной сериализации).</summary>
    private static string? SerializeValue(object? value)
    {
        if (value == null || value is System.Collections.ICollection { Count: 0 })
            return null;
        return Serializer.Serialize(value);
    }

    // ─── Автоматизация и группы из UI ─────────────────────────────────────────

    /// <summary>Изменение с проверкой задач автоматизации: ошибки — исключение, файл не трогается.</summary>
    private Task UpdateValidatedAsync(Action<HlcConfig> mutate)
        => UpdateAsync(config =>
        {
            mutate(config);
            var errors = AutomationService.Validate(config);
            if (errors.Count > 0)
                throw new InvalidOperationException(string.Join("\n", errors));
        });

    /// <summary>Создать (originalName == null) или заменить задачу.</summary>
    public Task SaveAutomationTaskAsync(string? originalName, AutomationTask task)
        => UpdateValidatedAsync(config =>
        {
            var tasks = config.Modules.Automation.Tasks;
            var index = originalName == null ? -1 : tasks.FindIndex(t => t.Name.Equals(originalName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                tasks[index] = task;
            else
                tasks.Add(task);
        });

    public Task DeleteAutomationTaskAsync(string name)
        => UpdateValidatedAsync(config => config.Modules.Automation.Tasks.RemoveAll(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public Task SetAutomationTaskEnabledAsync(string name, bool enabled)
        => UpdateValidatedAsync(config =>
        {
            var task = config.Modules.Automation.Tasks.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (task != null)
                task.Enabled = enabled;
        });

    /// <summary>Создать / переименовать / изменить группу. Ссылки на старое имя в задачах переименовываются.</summary>
    public Task SaveGroupAsync(string? originalName, string name, List<string> members)
        => UpdateValidatedAsync(config =>
        {
            var groups = config.Modules.Power.Groups ??= new Dictionary<string, List<string>>();
            if (originalName != null && !originalName.Equals(name, StringComparison.Ordinal))
            {
                groups.Remove(originalName);
                foreach (var step in config.Modules.Automation.Tasks.SelectMany(t => t.Steps))
                    foreach (var list in new[] { step.Wake, step.WaitOnline, step.Backup, step.Shutdown, step.Reboot })
                        if (list != null)
                            for (var i = 0; i < list.Count; i++)
                                if (list[i].Equals(originalName, StringComparison.OrdinalIgnoreCase))
                                    list[i] = name;
            }
            groups[name] = members;
        });

    /// <summary>Удалить группу (не получится, если она используется в задачах — сработает проверка).</summary>
    public Task DeleteGroupAsync(string name)
        => UpdateValidatedAsync(config =>
        {
            config.Modules.Power.Groups?.Remove(name);
            if (config.Modules.Power.Groups is { Count: 0 })
                config.Modules.Power.Groups = null;
        });

    /// <summary>
    /// После деплоя: секция agent хоста (создаётся при необходимости) + секции power/fanControl/smart,
    /// если их не было — агент обслуживает все три модуля.
    /// </summary>
    public Task UpsertAgentHostAsync(string name, string ip, AgentSection agent)
        => UpdateAsync(config =>
        {
            // Тот же агент (ip + порт), иначе хост с этим IP без агента, иначе новый
            var host = config.Hosts.FirstOrDefault(h => h.Ip == ip && h.Port == agent.Port)
                       ?? config.Hosts.FirstOrDefault(h => h.Ip == ip && h.Agent == null);
            if (host == null)
            {
                host = new HostModel { Name = name, Ip = ip };
                config.Hosts.Add(host);
            }

            host.Agent = agent;
            host.Power ??= new PowerSection();
            host.FanControl ??= new FanControlSection();
            host.Smart ??= new SmartSection();
        });

    /// <summary>
    /// Убрать агент с хоста в конфиге: секции agent/fanControl/smart. Если у хоста не осталось
    /// других секций (power/backup) — удаляется весь хост.
    /// </summary>
    public Task RemoveAgentAsync(string ip, int port)
        => UpdateAsync(config =>
        {
            var host = config.Hosts.FirstOrDefault(h => h.Ip == ip && h.Port == port);
            if (host == null)
                return;

            host.Agent = null;
            host.FanControl = null;
            host.Smart = null;

            if (host.Power == null && host.Backup == null)
                config.Hosts.Remove(host);
        });

    public static string GenerateApiKey()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>HttpClient для запросов к агенту: таймаут + X-Api-Key хоста.</summary>
    public HttpClient CreateAgentClient(IHttpClientFactory factory, HostModel? host, int timeoutSeconds)
    {
        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        var key = host?.Agent?.ApiKey;
        if (!string.IsNullOrWhiteSpace(key))
            client.DefaultRequestHeaders.Add("X-Api-Key", key);

        return client;
    }

    // ─── Deploy Paths ─────────────────────────────────────────────────────────

    public string GetDeploySourcePath(string module, string os) => GetDeployConfig(module, os)?.SourcePath ?? "";

    public string GetDeployInstallPath(string module, string os) => GetDeployConfig(module, os)?.InstallPath ?? "";

    public string GetDeployExecutableName(string module, string os) => GetDeployConfig(module, os)?.ExecutableName ?? "";

    private ModuleDeployConfig? GetDeployConfig(string module, string os)
    {
        var deploy = GetHlcConfig().Deploy;
        var osConfig = os.Equals("Linux", StringComparison.OrdinalIgnoreCase) ? deploy.Linux : deploy.Windows;
        return osConfig.TryGetValue(module, out var moduleConfig) ? moduleConfig : null;
    }

    // ─── Загрузка / миграция ──────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                _logger.LogWarning("Config not found: {Path}, using defaults", _configPath);
                SetConfig(new HlcConfig());
                return;
            }

            var config = Deserialize(File.ReadAllText(_configPath));

            if (config.ConfigPaths != null)
                config = Migrate(config);

            SetConfig(config);
            _logger.LogInformation("Loaded config from {Path}: {Hosts} hosts", _configPath, config.Hosts.Count);
        }
        catch (Exception ex)
        {
            // Оставляем предыдущий рабочий конфиг — битый файл не должен обнулять хосты
            _logger.LogError(ex, "Error loading config {Path}, keeping previous one", _configPath);
        }
    }

    /// <summary>
    /// Однократная миграция со старой схемы: HomeLabControl.yaml (configPaths + deploy)
    /// + PowerControl.yaml + FanControl.yaml + BackupManager.yaml → один файл с секциями модулей.
    /// Хосты сливаются по IP. Старые файлы переименовываются в *.migrated, старый HomeLabControl.yaml → *.bak.
    /// </summary>
    private HlcConfig Migrate(HlcConfig config)
    {
        var paths = config.ConfigPaths!;
        _logger.LogWarning("Old config format detected, migrating to unified {Path}", _configPath);

        var lenient = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var migratedFiles = new List<string>();

        HostModel GetOrAdd(string name, string ip)
        {
            var host = config.Hosts.FirstOrDefault(h => h.Ip == ip);
            if (host == null)
            {
                host = new HostModel { Name = name, Ip = ip };
                config.Hosts.Add(host);
            }
            return host;
        }

        // PowerControl.yaml: settings → modules.power, hosts → секция power (+ agent, если указан порт)
        if (TryRead(paths.PowerControl, out var powerYaml))
        {
            var power = lenient.Deserialize<LegacyPowerControlConfig>(powerYaml) ?? new();
            config.Modules.Power = power.Settings;

            foreach (var old in power.Hosts)
            {
                var host = GetOrAdd(old.Name, old.Ip);
                host.Mac = string.IsNullOrWhiteSpace(old.Mac) ? null : old.Mac.Trim();
                host.Power = new PowerSection { DefaultDelaySeconds = old.DefaultDelaySeconds };
                if (old.Port > 0)
                    host.Agent = new AgentSection { Port = old.Port };
            }
            migratedFiles.Add(paths.PowerControl!);
        }

        // FanControl.yaml: settings → modules.fanControl, servers → agent + fanControl + smart
        // (SMART Monitor раньше брал список серверов из FanControl.yaml)
        if (TryRead(paths.FanControl, out var fanYaml))
        {
            var fan = lenient.Deserialize<LegacyFanControlConfig>(fanYaml) ?? new();
            config.Modules.FanControl = fan.Settings;

            foreach (var server in fan.Servers)
            {
                var host = GetOrAdd(server.Name, server.Ip);

                // Порт из FanControl.yaml — точно порт агента; в PowerControl.yaml мог остаться порт старой службы
                if (host.Agent != null && host.Agent.Port != server.Port)
                    _logger.LogWarning("Host {Host}: agent port {OldPort} (PowerControl.yaml) replaced with {Port} (FanControl.yaml)",
                        host.Name, host.Agent.Port, server.Port);

                host.Agent ??= new AgentSection();
                host.Agent.Port = server.Port;
                host.FanControl = new FanControlSection
                {
                    IgnoredSensors = server.IgnoredSensors.Count > 0 ? server.IgnoredSensors : null
                };
                host.Smart = new SmartSection();
            }
            migratedFiles.Add(paths.FanControl!);
        }

        // BackupManager.yaml (snake_case): settings/storages/templates → modules.backup, hosts → секция backup
        if (TryRead(paths.BackupManager, out var backupYaml))
        {
            var backup = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<BackupManagerConfig>(backupYaml) ?? new();

            config.Modules.Backup = new BackupModuleConfig
            {
                Settings = backup.Settings,
                Storages = backup.Storages,
                Templates = backup.Templates
            };

            foreach (var old in backup.Hosts)
            {
                var host = GetOrAdd(old.Name, old.Ip);
                host.Backup = new BackupSection
                {
                    SshPort = old.Port,
                    User = old.User,
                    SshKey = old.SshKey,
                    Direction = old.BackupDirection,
                    Template = old.Template,
                    Storage = old.Storage,
                    Schedule = old.Schedule,
                    Enabled = old.Enabled
                };
            }
            migratedFiles.Add(paths.BackupManager!);
        }

        config.ConfigPaths = null;

        try
        {
            File.Copy(_configPath, _configPath + ".bak", overwrite: true);
            WriteFileAsync(Serialize(config)).GetAwaiter().GetResult();

            foreach (var file in migratedFiles)
                File.Move(file.Trim(), file.Trim() + ".migrated", overwrite: true);

            _logger.LogWarning("Migration done: {Hosts} hosts. Old files renamed to *.migrated, previous config saved as {Bak}",
                config.Hosts.Count, _configPath + ".bak");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write migrated config — using it in memory only");
        }

        return config;
    }

    private bool TryRead(string? path, out string content)
    {
        content = "";
        if (string.IsNullOrWhiteSpace(path))
            return false;

        path = path.Trim();
        if (!File.Exists(path))
        {
            _logger.LogWarning("Migration: {Path} not found, skipped", path);
            return false;
        }

        content = File.ReadAllText(path);
        return true;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private void SetConfig(HlcConfig config)
    {
        lock (_lock) { _config = config; }
        Changed?.Invoke();
    }

    private async Task WriteFileAsync(string yaml)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_configPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Собственную запись watcher'ом не перечитываем
        _ignoreWatcherUntil = DateTime.UtcNow.AddSeconds(2);

        var tempPath = _configPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, yaml);
        File.Move(tempPath, _configPath, overwrite: true);
    }

    private void SetupFileWatcher()
    {
        try
        {
            var fullPath = Path.GetFullPath(_configPath);
            var directory = Path.GetDirectoryName(fullPath) ?? ".";
            if (!Directory.Exists(directory))
                return;

            _watcher = new FileSystemWatcher(directory, Path.GetFileName(fullPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };

            FileSystemEventHandler onChange = (_, _) =>
            {
                if (DateTime.UtcNow < _ignoreWatcherUntil)
                    return;

                _logger.LogInformation("Config changed on disk, reloading...");
                Thread.Sleep(200);
                Load();
            };
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Renamed += (s, e) => onChange(s, e);

            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set up file watcher for {Path}", _configPath);
        }
    }

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .WithIndentedSequences()
        .Build();

    internal static HlcConfig Deserialize(string yaml)
    {
        var config = Deserializer.Deserialize<HlcConfig>(yaml) ?? new HlcConfig();

        // Отдельного ключа для HA больше нет: старое поле haApiKey читается и при сохранении пропадает
        foreach (var host in config.Hosts)
        {
            if (host.Agent != null)
                host.Agent.HaApiKey = null;
        }

        return config;
    }

    internal static string Serialize(HlcConfig config) => Serializer.Serialize(config);

    public void Dispose() => _watcher?.Dispose();

    // ─── Форматы старых файлов (только для миграции) ──────────────────────────

    private class LegacyPowerControlConfig
    {
        public PowerControlSettings Settings { get; set; } = new();
        public List<LegacyPowerHost> Hosts { get; set; } = new();
    }

    private class LegacyPowerHost
    {
        public string Name { get; set; } = "";
        public string Ip { get; set; } = "";
        public string? Mac { get; set; }
        public int Port { get; set; }
        public int? DefaultDelaySeconds { get; set; }
    }

    private class LegacyFanControlConfig
    {
        public FanControlSettings Settings { get; set; } = new();
        public List<LegacyFanServer> Servers { get; set; } = new();
    }

    private class LegacyFanServer
    {
        public string Name { get; set; } = "";
        public string Ip { get; set; } = "";
        public int Port { get; set; }
        public List<string> IgnoredSensors { get; set; } = new();
    }
}
