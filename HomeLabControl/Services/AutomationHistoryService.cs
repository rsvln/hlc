using System.Text.Json;
using HomeLabControl.Models;

namespace HomeLabControl.Services;

/// <summary>История запусков автоматизации: config/automation-history.json, последние запуски по каждой задаче.</summary>
public class AutomationHistoryService
{
    private const int KeepPerTask = 30;
    private const int MaxLogChars = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<AutomationHistoryService> _logger;
    private readonly object _lock = new();
    private Dictionary<string, List<AutomationRun>> _history = new();

    public AutomationHistoryService(HlcConfigService configService, ILogger<AutomationHistoryService> logger)
    {
        _logger = logger;
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configService.ConfigPath)) ?? ".";
        _path = Path.Combine(configDir, "automation-history.json");
        Load();
    }

    /// <summary>Запуски задачи, новые первыми.</summary>
    public List<AutomationRun> Get(string taskName)
    {
        lock (_lock)
            return _history.TryGetValue(taskName, out var runs) ? runs.ToList() : new();
    }

    public AutomationRun? GetLast(string taskName) => Get(taskName).FirstOrDefault();

    public void Add(AutomationRun run)
    {
        var stored = new AutomationRun
        {
            TaskName = run.TaskName,
            Trigger = run.Trigger,
            User = run.User,
            StartTime = run.StartTime,
            EndTime = run.EndTime,
            Status = run.Status,
            Log = run.Log.Length > MaxLogChars ? "…(truncated)\n" + run.Log[^MaxLogChars..] : run.Log
        };

        lock (_lock)
        {
            if (!_history.TryGetValue(run.TaskName, out var runs))
                _history[run.TaskName] = runs = new List<AutomationRun>();

            runs.Insert(0, stored);
            if (runs.Count > KeepPerTask)
                runs.RemoveRange(KeepPerTask, runs.Count - KeepPerTask);

            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _history = JsonSerializer.Deserialize<Dictionary<string, List<AutomationRun>>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read automation history {Path}", _path);
        }
    }

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_history, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save automation history {Path}", _path);
        }
    }
}
