// Services/PowerScheduler.cs
namespace HomeLabControlAgent.Services;

using System.Text.Json;
using HomeLabControlAgent.Models;

/// <summary>
/// Отложенное выключение/перезагрузка внутри процесса агента.
/// HTTP-запрос возвращается сразу, действие можно отменить через /api/power/cancel.
/// Одинаково работает на Windows и Linux (не зависит от shutdown /t и systemctl --when).
///
/// Запланированное действие сохраняется в power-pending.json и восстанавливается после перезапуска агента —
/// но только в пределах той же загрузки ОС: если машина успела перезагрузиться, действие отбрасывается,
/// иначе она выключилась бы сразу после старта.
/// </summary>
public class PowerScheduler : IDisposable
{
    private static readonly string StateFile = Path.Combine(AppContext.BaseDirectory, "power-pending.json");

    private readonly ILogger _logger;
    private readonly Func<PendingPowerAction, PowerResponse> _execute;
    private readonly object _lock = new();
    private CancellationTokenSource? _pendingCts;
    private PendingPowerAction? _pending;

    public PowerScheduler(ILogger logger, Func<PendingPowerAction, PowerResponse> execute)
    {
        _logger = logger;
        _execute = execute;
        RestorePending();
    }

    public PendingPowerAction? Pending
    {
        get { lock (_lock) return _pending; }
    }

    /// <summary>Запланировать действие; предыдущее запланированное отменяется.</summary>
    public PowerResponse Schedule(PowerAction action, int delaySeconds, bool force = true)
    {
        var pending = new PendingPowerAction
        {
            Action = action,
            Force = force,
            ScheduledAt = DateTime.UtcNow,
            ExecuteAt = DateTime.UtcNow.AddSeconds(delaySeconds)
        };

        Start(pending);
        SaveState(pending);

        _logger.LogInformation("{Action} scheduled in {Delay}s", action, delaySeconds);
        return new PowerResponse
        {
            Success = true,
            Message = $"{action} scheduled in {delaySeconds} seconds"
        };
    }

    /// <summary>Отменить запланированное действие. true — было что отменять.</summary>
    public bool Cancel()
    {
        bool cancelled;
        lock (_lock)
        {
            cancelled = CancelPendingLocked();
        }

        DeleteState();
        return cancelled;
    }

    private void Start(PendingPowerAction pending)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            CancelPendingLocked();
            cts = new CancellationTokenSource();
            _pendingCts = cts;
            _pending = pending;
        }

        var delay = pending.ExecuteAt - DateTime.UtcNow;
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_lock)
            {
                if (_pendingCts != cts)
                    return;
                _pending = null;
                _pendingCts = null;
            }

            // Файл удаляется до выполнения: после перезагрузки действие не должно повториться
            DeleteState();

            var result = _execute(pending);
            if (!result.Success)
                _logger.LogError("Scheduled {Action} failed: {Error}", pending.Action, result.Error);
        });
    }

    private bool CancelPendingLocked()
    {
        if (_pendingCts == null)
            return false;

        _pendingCts.Cancel();
        _pendingCts.Dispose();
        _pendingCts = null;

        _logger.LogInformation("Pending {Action} cancelled", _pending?.Action);
        _pending = null;
        return true;
    }

    // ─── Сохранение между перезапусками агента ────────────────────────────────

    private static DateTime BootTimeUtc => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    private void RestorePending()
    {
        try
        {
            if (!File.Exists(StateFile))
                return;

            var pending = JsonSerializer.Deserialize<PendingPowerAction>(File.ReadAllText(StateFile));
            if (pending == null)
            {
                DeleteState();
                return;
            }

            // Запланировано до текущей загрузки ОС — машина уже перезагружалась, действие неактуально.
            // Минута запаса на неточность времени загрузки.
            if (pending.ScheduledAt < BootTimeUtc.AddMinutes(1))
            {
                _logger.LogWarning("Discarding pending {Action} scheduled at {At:u}: the machine has rebooted since",
                    pending.Action, pending.ScheduledAt);
                DeleteState();
                return;
            }

            _logger.LogWarning("Restoring pending {Action} at {At:u} after agent restart", pending.Action, pending.ExecuteAt);
            Start(pending);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore pending power action");
            DeleteState();
        }
    }

    private void SaveState(PendingPowerAction pending)
    {
        try
        {
            File.WriteAllText(StateFile, JsonSerializer.Serialize(pending));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save pending power action — it will not survive an agent restart");
        }
    }

    private static void DeleteState()
    {
        try { File.Delete(StateFile); } catch { /* нет файла или нет прав — не критично */ }
    }

    public void Dispose()
    {
        // При остановке агента запланированное действие остаётся в файле (восстановится при старте)
        lock (_lock)
        {
            _pendingCts?.Cancel();
            _pendingCts = null;
        }
    }
}
