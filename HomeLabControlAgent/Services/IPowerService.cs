// Services/IPowerService.cs
namespace HomeLabControlAgent.Services;

using HomeLabControlAgent.Models;

public interface IPowerService
{
    /// <summary>
    /// delay = 0 — сразу, иначе — отменяемое отложенное действие.
    /// force = false — Windows: без принудительного закрытия приложений (несохранённые документы могут
    /// остановить выключение); на Linux не влияет — systemctl poweroff/reboot всегда штатные.
    /// </summary>
    Task<PowerResponse> ShutdownAsync(int delay = 0, bool force = true);
    Task<PowerResponse> RebootAsync(int delay = 0, bool force = true);
    Task<PowerResponse> ShutdownWithDialogAsync(int delay = 30, string message = "Компьютер будет выключен");
    Task<PowerResponse> RebootWithDialogAsync(int delay = 30, string message = "Компьютер будет перезагружен");
    Task<PowerResponse> CancelShutdownAsync();

    /// <summary>Запланированное действие, если есть.</summary>
    PendingPowerAction? GetPending();
}
