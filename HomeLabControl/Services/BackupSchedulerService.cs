using System.Collections.Concurrent;
using Cronos;

namespace HomeLabControl.Services;

/// <summary>
/// Запуск бэкапов по расписанию: hosts[].backup.schedule — cron из 5 полей ("0 3 * * *"),
/// время — часовой пояс контейнера (переменная TZ). Хосты с enabled: false пропускаются.
/// Одновременно выполняется не больше modules.backup.settings.maxParallelBackups (ограничение в BackupManagerService).
/// </summary>
public class BackupSchedulerService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private readonly BackupManagerService _backup;
    private readonly HlcConfigService _configService;
    private readonly ILogger<BackupSchedulerService> _logger;

    // host → (расписание, следующий запуск). Пересчитывается, если расписание в конфиге поменялось.
    private readonly ConcurrentDictionary<string, (string Schedule, DateTimeOffset? Next)> _plan = new();
    private readonly HashSet<string> _invalidReported = new();

    public BackupSchedulerService(BackupManagerService backup, HlcConfigService configService, ILogger<BackupSchedulerService> logger)
    {
        _backup = backup;
        _configService = configService;
        _logger = logger;
    }

    /// <summary>Следующий запуск по расписанию (null — расписания нет или хост выключен).</summary>
    public DateTimeOffset? GetNextRun(string hostName)
    {
        var host = _configService.GetBackupHosts().FirstOrDefault(h => h.Name == hostName);
        if (host?.Backup is not { Enabled: true } section || string.IsNullOrWhiteSpace(section.Schedule))
            return null;

        return TryParse(section.Schedule, out var cron) ? cron!.GetNextOccurrence(DateTimeOffset.Now, TimeZoneInfo.Local) : null;
    }

    public static bool TryParse(string schedule, out CronExpression? cron)
    {
        try
        {
            cron = CronExpression.Parse(schedule.Trim(), CronFormat.Standard);
            return true;
        }
        catch (CronFormatException)
        {
            cron = null;
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Backup scheduler started (time zone {Tz})", TimeZoneInfo.Local.Id);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CheckSchedules();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backup scheduler tick failed");
            }

            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void CheckSchedules()
    {
        var now = DateTimeOffset.Now;
        var hosts = _configService.GetBackupHosts();

        foreach (var host in hosts)
        {
            var section = host.Backup!;
            if (!section.Enabled || string.IsNullOrWhiteSpace(section.Schedule))
            {
                _plan.TryRemove(host.Name, out _);
                continue;
            }

            if (!TryParse(section.Schedule, out var cron))
            {
                if (_invalidReported.Add($"{host.Name}|{section.Schedule}"))
                    _logger.LogError("Invalid backup schedule '{Schedule}' for {Host} — expected cron with 5 fields, e.g. \"0 3 * * *\"",
                        section.Schedule, host.Name);
                continue;
            }

            // Новое/изменённое расписание — считаем следующий запуск от текущего момента (без догоняющих запусков)
            if (!_plan.TryGetValue(host.Name, out var plan) || plan.Schedule != section.Schedule)
            {
                var next = cron!.GetNextOccurrence(now, TimeZoneInfo.Local);
                _plan[host.Name] = (section.Schedule, next);
                _logger.LogInformation("Backup of {Host} scheduled: {Schedule}, next at {Next:yyyy-MM-dd HH:mm}", host.Name, section.Schedule, next);
                continue;
            }

            if (plan.Next is { } due && now >= due)
            {
                _plan[host.Name] = (section.Schedule, cron!.GetNextOccurrence(now, TimeZoneInfo.Local));
                var hostName = host.Name;
                _ = Task.Run(async () =>
                {
                    _logger.LogInformation("Scheduled backup of {Host} started", hostName);
                    await _backup.RunBackupAsync(hostName, trigger: "schedule");
                });
            }
        }

        // Хосты, удалённые из конфига
        foreach (var name in _plan.Keys.Except(hosts.Select(h => h.Name)).ToList())
            _plan.TryRemove(name, out _);
    }
}
