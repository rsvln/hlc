using System.Text.Json;
using HomeLabControl.Models;

namespace HomeLabControl.Services;

/// <summary>
/// История запусков бэкапа: config/backup-history.json (последние запуски по каждому хосту)
/// и строка на запуск в текстовый лог modules.backup.settings.logPath.
/// </summary>
public class BackupHistoryService
{
    private const int KeepPerHost = 30;
    private const int MaxLogChars = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly HlcConfigService _configService;
    private readonly ILogger<BackupHistoryService> _logger;
    private readonly object _lock = new();
    private Dictionary<string, List<BackupJob>> _history = new();

    public BackupHistoryService(HlcConfigService configService, ILogger<BackupHistoryService> logger)
    {
        _configService = configService;
        _logger = logger;
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configService.ConfigPath)) ?? ".";
        _path = Path.Combine(configDir, "backup-history.json");
        Load();
    }

    /// <summary>Запуски хоста, новые первыми.</summary>
    public List<BackupJob> Get(string hostName)
    {
        lock (_lock)
            return _history.TryGetValue(hostName, out var jobs) ? jobs.ToList() : new();
    }

    public BackupJob? GetLast(string hostName) => Get(hostName).FirstOrDefault();

    /// <summary>Последний успешный.</summary>
    public BackupJob? GetLastSuccess(string hostName) => Get(hostName).FirstOrDefault(j => j.Status == "Success");

    public void Add(BackupJob job)
    {
        var stored = new BackupJob
        {
            HostName = job.HostName,
            StartTime = job.StartTime,
            EndTime = job.EndTime,
            Status = job.Status,
            Trigger = job.Trigger,
            SizeBytes = job.SizeBytes,
            Log = job.Log.Length > MaxLogChars ? "…(truncated)\n" + job.Log[^MaxLogChars..] : job.Log
        };

        lock (_lock)
        {
            if (!_history.TryGetValue(job.HostName, out var jobs))
                _history[job.HostName] = jobs = new List<BackupJob>();

            jobs.Insert(0, stored);
            if (jobs.Count > KeepPerHost)
                jobs.RemoveRange(KeepPerHost, jobs.Count - KeepPerHost);

            Save();
        }

        AppendLogFile(stored);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _history = JsonSerializer.Deserialize<Dictionary<string, List<BackupJob>>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read {Path}, starting with empty backup history", _path);
        }
    }

    // Вызывать под _lock
    private void Save()
    {
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_history, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save backup history to {Path}", _path);
        }
    }

    private void AppendLogFile(BackupJob job)
    {
        var logPath = _configService.GetBackupModule().Settings.LogPath;
        if (string.IsNullOrWhiteSpace(logPath))
            return;

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(logPath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var duration = job.EndTime.HasValue ? $"{(job.EndTime.Value - job.StartTime).TotalSeconds:F0}s" : "-";
            var header = $"{job.StartTime:yyyy-MM-dd HH:mm:ss} {job.HostName} {job.Status} ({job.Trigger}, {duration})";
            File.AppendAllText(logPath, header + Environment.NewLine +
                                        string.Join(Environment.NewLine, job.Log.TrimEnd().Split('\n').Select(l => "    " + l.TrimEnd('\r'))) +
                                        Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write backup log {Path}", logPath);
        }
    }
}
