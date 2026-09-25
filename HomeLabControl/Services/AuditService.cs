using System.Text.Json;

namespace HomeLabControl.Services;

public class AuditEntry
{
    public DateTime Time { get; set; }
    public string User { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string? Details { get; set; }
    public bool Success { get; set; } = true;
    public string? Ip { get; set; }
}

/// <summary>
/// Журнал действий: config/audit.log, одна JSON-строка на запись (кто, когда, что, над чем, результат).
/// При 5 МБ файл переименовывается в audit.log.1 (хранится одна предыдущая часть).
/// </summary>
public class AuditService
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly string _path;
    private readonly ILogger<AuditService> _logger;
    private readonly object _lock = new();

    public AuditService(HlcConfigService configService, ILogger<AuditService> logger)
    {
        _logger = logger;
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configService.ConfigPath)) ?? ".";
        _path = Path.Combine(configDir, "audit.log");
    }

    public void Log(string? user, string action, string? target = null, string? details = null, bool success = true, string? ip = null)
    {
        var entry = new AuditEntry
        {
            Time = DateTime.Now,
            User = string.IsNullOrEmpty(user) ? "?" : user,
            Action = action,
            Target = target,
            Details = details,
            Success = success,
            Ip = ip
        };

        _logger.LogInformation("AUDIT {User} {Action} {Target} {Details} {Result}",
            entry.User, action, target, details, success ? "ok" : "FAILED");

        try
        {
            lock (_lock)
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(_path, _path + ".1", overwrite: true);

                File.AppendAllText(_path, JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit log {Path}", _path);
        }
    }

    /// <summary>Последние записи, новые первыми.</summary>
    public List<AuditEntry> Read(int max = 500)
    {
        var result = new List<AuditEntry>();
        lock (_lock)
        {
            foreach (var file in new[] { _path, _path + ".1" })
            {
                if (!File.Exists(file))
                    continue;

                foreach (var line in File.ReadLines(file).Reverse())
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<AuditEntry>(line) is { } entry)
                            result.Add(entry);
                    }
                    catch (JsonException)
                    {
                    }

                    if (result.Count >= max)
                        return result;
                }
            }
        }
        return result;
    }
}
