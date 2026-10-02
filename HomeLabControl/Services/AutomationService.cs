using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using HomeLabControl.Models;
using Host = HomeLabControl.Models.Host;

namespace HomeLabControl.Services;

/// <summary>
/// Автоматизация питания (modules.automation.tasks): задачи по cron из шагов wake / waitOnline / backup / shutdown / reboot / delay.
/// Шаги идут по порядку; ошибка останавливает задачу, кроме шагов с always: true. shutdown / reboot трогают
/// только хосты, которые разбудила эта же задача (хост, включённый до неё, остаётся включённым), если не force: true.
/// </summary>
public class AutomationService : BackgroundService
{
    public const string AuditUser = "automation";

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OnlinePoll = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WolRepeat = TimeSpan.FromMinutes(1);
    private const int DefaultTimeoutMinutes = 10;

    private readonly HlcConfigService _config;
    private readonly PowerControlService _power;
    private readonly BackupManagerService _backup;
    private readonly AgentStatusService _status;
    private readonly AgentMonitorService _monitor;
    private readonly AutomationHistoryService _history;
    private readonly AuditService _audit;
    private readonly ILogger<AutomationService> _logger;

    private readonly ConcurrentDictionary<string, (string Schedule, DateTimeOffset? Next)> _plan = new();
    private readonly ConcurrentDictionary<string, AutomationRun> _running = new();
    private readonly HashSet<string> _invalidReported = new();
    private CancellationToken _stopping = CancellationToken.None;

    /// <summary>Запуск начался / шаг завершился / запуск закончился — для обновления страницы.</summary>
    public event Action? Changed;

    public AutomationService(HlcConfigService config, PowerControlService power, BackupManagerService backup, AgentStatusService status,
        AgentMonitorService monitor, AutomationHistoryService history, AuditService audit, ILogger<AutomationService> logger)
    {
        _config = config;
        _power = power;
        _backup = backup;
        _status = status;
        _monitor = monitor;
        _history = history;
        _audit = audit;
        _logger = logger;
    }

    public List<AutomationTask> GetTasks() => _config.GetHlcConfig().Modules.Automation.Tasks;

    public AutomationRun? GetRunning(string taskName) => _running.TryGetValue(taskName, out var run) ? run : null;

    public DateTimeOffset? GetNextRun(AutomationTask task)
    {
        if (!task.Enabled || string.IsNullOrWhiteSpace(task.Schedule) || !BackupSchedulerService.TryParse(task.Schedule, out var cron))
            return null;
        return cron!.GetNextOccurrence(DateTimeOffset.Now, TimeZoneInfo.Local);
    }

    // ─── Проверка конфига ─────────────────────────────────────────────────────

    /// <summary>Ошибки в задачах: пустое имя / повтор, битый cron, шаг без типа или с двумя типами, неизвестный хост / группа.</summary>
    public static List<string> Validate(HlcConfig config)
    {
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in config.Modules.Automation.Tasks)
        {
            var label = string.IsNullOrWhiteSpace(task.Name) ? "(no name)" : task.Name;
            if (string.IsNullOrWhiteSpace(task.Name))
                errors.Add("automation: a task without a name");
            else if (!names.Add(task.Name))
                errors.Add($"automation: duplicate task name '{task.Name}'");

            if (!string.IsNullOrWhiteSpace(task.Schedule) && !BackupSchedulerService.TryParse(task.Schedule, out _))
                errors.Add($"automation '{label}': invalid schedule '{task.Schedule}' (cron with 5 fields, e.g. \"0 3 * * *\")");

            if (task.Steps.Count == 0)
                errors.Add($"automation '{label}': no steps");

            for (var i = 0; i < task.Steps.Count; i++)
            {
                var step = task.Steps[i];
                if (step.KindCount != 1)
                {
                    errors.Add($"automation '{label}', step {i + 1}: exactly one of wake / waitOnline / backup / shutdown / reboot / delay is required");
                    continue;
                }

                if (step.Kind == "delay")
                {
                    if (step.Delay is < 0 or > 86400)
                        errors.Add($"automation '{label}', step {i + 1}: delay must be 0..86400 seconds");
                    continue;
                }

                HlcConfigService.ResolveTargets(config, step.Targets, out var unknown);
                if (step.Targets.Count == 0)
                    errors.Add($"automation '{label}', step {i + 1}: {step.Kind} needs at least one host or group");
                foreach (var name in unknown)
                    errors.Add($"automation '{label}', step {i + 1}: unknown host or group '{name}'");
            }
        }

        return errors;
    }

    // ─── Расписание ───────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _logger.LogInformation("Automation scheduler started (time zone {Tz})", TimeZoneInfo.Local.Id);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CheckSchedules();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automation scheduler tick failed");
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
        var tasks = GetTasks();

        foreach (var task in tasks)
        {
            if (!task.Enabled || string.IsNullOrWhiteSpace(task.Schedule) || string.IsNullOrWhiteSpace(task.Name))
            {
                _plan.TryRemove(task.Name, out _);
                continue;
            }

            if (!BackupSchedulerService.TryParse(task.Schedule, out var cron))
            {
                if (_invalidReported.Add($"{task.Name}|{task.Schedule}"))
                    _logger.LogError("Invalid automation schedule '{Schedule}' for {Task}", task.Schedule, task.Name);
                continue;
            }

            // Новое / изменённое расписание — следующий запуск от текущего момента (без догоняющих запусков)
            if (!_plan.TryGetValue(task.Name, out var plan) || plan.Schedule != task.Schedule)
            {
                var next = cron!.GetNextOccurrence(now, TimeZoneInfo.Local);
                _plan[task.Name] = (task.Schedule, next);
                _logger.LogInformation("Automation {Task} scheduled: {Schedule}, next at {Next:yyyy-MM-dd HH:mm}", task.Name, task.Schedule, next);
                continue;
            }

            if (plan.Next is { } due && now >= due)
            {
                _plan[task.Name] = (task.Schedule, cron!.GetNextOccurrence(now, TimeZoneInfo.Local));
                var name = task.Name;
                _ = Task.Run(() => RunAsync(name, "schedule", AuditUser));
            }
        }

        foreach (var name in _plan.Keys.Except(tasks.Select(t => t.Name)).ToList())
            _plan.TryRemove(name, out _);
    }

    // ─── Запуск ───────────────────────────────────────────────────────────────

    /// <summary>Выполнить задачу (шаги по порядку). Если она уже выполняется — запуск Skipped.</summary>
    public async Task<AutomationRun> RunAsync(string taskName, string trigger, string user)
    {
        var run = new AutomationRun { TaskName = taskName, Trigger = trigger, User = user, StartTime = DateTime.Now };
        var task = GetTasks().FirstOrDefault(t => t.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase));
        if (task == null)
        {
            run.Status = "Failed";
            run.EndTime = DateTime.Now;
            run.Log = $"Task {taskName} not found\n";
            return run;
        }

        run.TaskName = task.Name;
        if (!_running.TryAdd(task.Name, run))
        {
            run.Status = "Skipped";
            run.EndTime = DateTime.Now;
            run.Log = $"[{DateTime.Now:HH:mm:ss}] {task.Name} is already running\n";
            return run;
        }

        var woken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = false;
        Notify();

        try
        {
            Log(run, $"Task {task.Name} started ({trigger}{(user != AuditUser ? $" by {user}" : "")})");

            for (var i = 0; i < task.Steps.Count; i++)
            {
                var step = task.Steps[i];
                if (failed && step.Always != true)
                {
                    Log(run, $"Step {i + 1} skipped: {step}");
                    continue;
                }

                Log(run, $"Step {i + 1}: {step}");
                Notify();
                try
                {
                    var ok = await RunStepAsync(run, step, woken);
                    if (!ok)
                    {
                        failed = true;
                        Log(run, $"Step {i + 1} FAILED");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed = true;
                    Log(run, $"Step {i + 1} FAILED: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            failed = true;
            Log(run, "Stopped: HomeLabControl is shutting down");
        }
        catch (Exception ex)
        {
            failed = true;
            Log(run, $"FAILED: {ex.Message}");
        }
        finally
        {
            run.Status = failed ? "Failed" : "Success";
            run.EndTime = DateTime.Now;
            Log(run, $"Task {task.Name}: {run.Status} in {(run.EndTime.Value - run.StartTime):hh\\:mm\\:ss}");
            _running.TryRemove(task.Name, out _);
            _history.Add(run);
            _audit.Log(user, "automation.run", task.Name, $"{trigger}: {run.Status}", !failed, null);
            Notify();
        }

        // В уведомлении — что именно не получилось (строки с ошибками), при успехе — коротко
        var problems = run.Log.Split('\n')
            .Where(l => l.Contains("FAILED") || l.Contains("timeout") || l.Contains("cannot") || l.Contains("unknown"))
            .Select(l => l.Trim()).Take(5).ToList();
        _monitor.Raise(new HlcEvent
        {
            Type = failed ? "automationFailed" : "automationSuccess",
            Host = task.Name,
            Title = failed ? $"Automation {task.Name} failed" : $"Automation {task.Name} done",
            Message = failed && problems.Count > 0
                ? string.Join("\n", problems)
                : $"{task.Steps.Count} step(s) in {(run.EndTime!.Value - run.StartTime):hh\\:mm\\:ss}"
        });

        return run;
    }

    private async Task<bool> RunStepAsync(AutomationRun run, AutomationStep step, HashSet<string> woken)
    {
        var ct = _stopping;

        if (step.Kind == "delay")
        {
            await Task.Delay(TimeSpan.FromSeconds(step.Delay ?? 0), ct);
            return true;
        }

        var hosts = _config.ResolveTargets(step.Targets, out var unknown);
        if (unknown.Count > 0)
        {
            Log(run, $"  unknown host / group: {string.Join(", ", unknown)}");
            return false;
        }

        switch (step.Kind)
        {
            case "wake":
                return await WakeAsync(run, hosts, step, woken, ct);

            case "waitOnline":
                return await WaitOnlineAsync(run, hosts, Timeout(step), wol: null, ct);

            case "backup":
            {
                var withBackup = hosts.Where(h => h.Backup != null).ToList();
                foreach (var h in hosts.Except(withBackup))
                    Log(run, $"  {h.Name}: no backup section — skipped");

                // Параллельно: ограничение maxParallelBackups внутри BackupManagerService
                var jobs = await Task.WhenAll(withBackup.Select(h => _backup.RunBackupAsync(h.Name, "automation")));
                foreach (var job in jobs)
                    Log(run, $"  {job.HostName}: backup {job.Status}");
                return jobs.All(j => j.Status == "Success");
            }

            case "shutdown":
            case "reboot":
                return await PowerAsync(run, hosts, step, woken);

            default:
                Log(run, $"  unknown step type");
                return false;
        }
    }

    private static TimeSpan Timeout(AutomationStep step)
        => TimeSpan.FromMinutes(Math.Clamp(step.TimeoutMinutes ?? DefaultTimeoutMinutes, 1, 24 * 60));

    private async Task<bool> WakeAsync(AutomationRun run, List<Host> hosts, AutomationStep step, HashSet<string> woken, CancellationToken ct)
    {
        var toWake = new List<Host>();
        foreach (var host in hosts)
        {
            if (await IsOnlineAsync(host))
            {
                Log(run, $"  {host.Name}: already on — will not be shut down by this task");
                continue;
            }

            if (string.IsNullOrWhiteSpace(host.Mac))
            {
                Log(run, $"  {host.Name}: offline and has no MAC — cannot wake");
                return false;
            }

            toWake.Add(host);
            woken.Add(host.Name);
            var (sent, via) = await _power.WakeAsync(host);
            if (!sent)
            {
                Log(run, $"  {host.Name}: WOL FAILED — {via}");
                return false;
            }
            Log(run, $"  {host.Name}: WOL sent ({via})");
        }

        if (toWake.Count == 0)
            return true;

        return await WaitOnlineAsync(run, toWake, Timeout(step), wol: toWake, ct);
    }

    /// <summary>Ждать, пока все хосты в сети. wol — кому повторять Wake-on-LAN раз в минуту, пока ждём.</summary>
    private async Task<bool> WaitOnlineAsync(AutomationRun run, List<Host> hosts, TimeSpan timeout, List<Host>? wol, CancellationToken ct)
    {
        var deadline = DateTime.Now + timeout;
        var lastWol = DateTime.Now;
        var pending = hosts.ToList();

        while (true)
        {
            foreach (var host in pending.ToList())
            {
                if (await IsOnlineAsync(host))
                {
                    pending.Remove(host);
                    Log(run, $"  {host.Name}: online");
                }
            }

            if (pending.Count == 0)
                return true;

            if (DateTime.Now >= deadline)
            {
                Log(run, $"  timeout {timeout.TotalMinutes:0} min: still offline — {string.Join(", ", pending.Select(h => h.Name))}");
                return false;
            }

            if (wol != null && DateTime.Now - lastWol >= WolRepeat)
            {
                foreach (var host in pending.Where(h => wol.Contains(h) && !string.IsNullOrWhiteSpace(h.Mac)))
                    await _power.WakeAsync(host);
                lastWol = DateTime.Now;
            }

            await Task.Delay(OnlinePoll, ct);
        }
    }

    private async Task<bool> PowerAsync(AutomationRun run, List<Host> hosts, AutomationStep step, HashSet<string> woken)
    {
        var ok = true;
        foreach (var host in hosts)
        {
            if (step.Force != true && !woken.Contains(host.Name))
            {
                Log(run, $"  {host.Name}: not woken by this task — left as is (force: true to {step.Kind} anyway)");
                continue;
            }

            if (!host.HasAgent)
            {
                Log(run, $"  {host.Name}: no agent — cannot {step.Kind}");
                ok = false;
                continue;
            }

            var delay = Math.Clamp(step.DelaySeconds ?? 0, 0, 86400);
            var success = step.Kind == "reboot"
                ? await _power.RebootAsync(host.Ip, host.Port, delay, false)
                : await _power.ShutdownAsync(host.Ip, host.Port, delay, false);
            Log(run, $"  {host.Name}: {step.Kind}{(delay > 0 ? $" in {delay}s" : "")} — {(success ? "ok" : "FAILED")}");
            ok &= success;
        }

        return ok;
    }

    /// <summary>Хост в сети: агент отвечает (если есть), иначе ping.</summary>
    private async Task<bool> IsOnlineAsync(Host host)
    {
        if (host.HasAgent)
        {
            var status = await _status.RefreshAsync(host);
            if (status.State is AgentState.Online or AgentState.Open or AgentState.Legacy)
                return true;
        }

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host.Ip, 1000);
            // С агентом ждём именно агента: ping отвечает раньше, чем поднимутся службы
            return reply.Status == IPStatus.Success && !host.HasAgent;
        }
        catch
        {
            return false;
        }
    }

    private void Log(AutomationRun run, string line)
    {
        lock (run)
            run.Log += $"[{DateTime.Now:HH:mm:ss}] {line}\n";
        _logger.LogInformation("Automation {Task}: {Line}", run.TaskName, line.Trim());
    }

    private void Notify()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Automation Changed handler failed");
        }
    }
}
