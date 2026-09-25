// Services/LinuxPowerService.cs
namespace HomeLabControlAgent.Services;

using HomeLabControlAgent.Models;
using System.Diagnostics;

public class LinuxPowerService : IPowerService, IDisposable
{
    private readonly ILogger<LinuxPowerService> _logger;
    private readonly PowerScheduler _scheduler;

    public LinuxPowerService(ILogger<LinuxPowerService> logger)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("LinuxPowerService only works on Linux");

        _logger = logger;
        _scheduler = new PowerScheduler(logger);
    }

    public Task<PowerResponse> ShutdownAsync(int delay = 0)
        => Task.FromResult(Execute(PowerAction.Shutdown, delay));

    public Task<PowerResponse> RebootAsync(int delay = 0)
        => Task.FromResult(Execute(PowerAction.Reboot, delay));

    public Task<PowerResponse> ShutdownWithDialogAsync(int delay = 30, string message = "Компьютер будет выключен")
        => Task.FromResult(DialogNotSupported());

    public Task<PowerResponse> RebootWithDialogAsync(int delay = 30, string message = "Компьютер будет перезагружен")
        => Task.FromResult(DialogNotSupported());

    public Task<PowerResponse> CancelShutdownAsync()
    {
        var cancelled = _scheduler.Cancel();
        return Task.FromResult(new PowerResponse
        {
            Success = true,
            Message = cancelled ? "Shutdown/reboot cancelled" : "Nothing to cancel"
        });
    }

    public PendingPowerAction? GetPending() => _scheduler.Pending;

    private PowerResponse Execute(PowerAction action, int delay)
    {
        if (delay > 0)
            return _scheduler.Schedule(action, delay, () => RunSystemctl(action));

        _scheduler.Cancel();
        return RunSystemctl(action);
    }

    /// <summary>
    /// Штатное выключение через systemd: службы останавливаются, ФС (и md-массивы) корректно отмонтируются.
    /// Раньше использовался "--force --force" — это аналог выдёргивания шнура.
    /// </summary>
    private PowerResponse RunSystemctl(PowerAction action)
    {
        var verb = action == PowerAction.Reboot ? "reboot" : "poweroff";
        try
        {
            _logger.LogInformation("Executing systemctl {Verb}", verb);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = verb,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true
            });

            if (process == null)
                return new PowerResponse { Success = false, Error = "Failed to start systemctl" };

            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15_000))
                return new PowerResponse { Success = true, Message = $"{action} initiated (systemctl still running)" };

            if (process.ExitCode != 0)
            {
                _logger.LogError("systemctl {Verb} failed with exit code {Code}: {Error}", verb, process.ExitCode, error);
                return new PowerResponse { Success = false, Error = $"Exit code: {process.ExitCode}, Error: {error}" };
            }

            return new PowerResponse { Success = true, Message = $"{action} initiated" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute systemctl {Verb}", verb);
            return new PowerResponse { Success = false, Error = ex.Message };
        }
    }

    private PowerResponse DialogNotSupported()
    {
        _logger.LogWarning("Dialog mode not supported on Linux");
        return new PowerResponse
        {
            Success = false,
            Error = "Dialog mode is only supported on Windows"
        };
    }

    public void Dispose() => _scheduler.Dispose();
}
