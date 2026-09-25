// Services/PowerScheduler.cs
namespace HomeLabControlAgent.Services;

using HomeLabControlAgent.Models;

/// <summary>
/// Отложенное выключение/перезагрузка внутри процесса агента.
/// HTTP-запрос возвращается сразу, действие можно отменить через /api/power/cancel.
/// Одинаково работает на Windows и Linux (не зависит от shutdown /t и systemctl --when).
/// </summary>
public class PowerScheduler : IDisposable
{
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private CancellationTokenSource? _pendingCts;
    private PendingPowerAction? _pending;

    public PowerScheduler(ILogger logger)
    {
        _logger = logger;
    }

    public PendingPowerAction? Pending
    {
        get { lock (_lock) return _pending; }
    }

    /// <summary>Запланировать действие; предыдущее запланированное отменяется.</summary>
    public PowerResponse Schedule(PowerAction action, int delaySeconds, Func<PowerResponse> execute)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            CancelPendingLocked();

            cts = new CancellationTokenSource();
            _pendingCts = cts;
            _pending = new PendingPowerAction
            {
                Action = action,
                ScheduledAt = DateTime.UtcNow,
                ExecuteAt = DateTime.UtcNow.AddSeconds(delaySeconds)
            };
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token);
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

            var result = execute();
            if (!result.Success)
                _logger.LogError("Scheduled {Action} failed: {Error}", action, result.Error);
        });

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
        lock (_lock)
        {
            return CancelPendingLocked();
        }
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

    public void Dispose()
    {
        Cancel();
    }
}
