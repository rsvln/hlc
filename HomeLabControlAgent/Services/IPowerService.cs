// Services/IPowerService.cs
namespace HomeLabControlAgent.Services;

using HomeLabControlAgent.Models;

public interface IPowerService
{
    /// <summary>delay = 0 — сразу, иначе — отменяемое отложенное выключение.</summary>
    Task<PowerResponse> ShutdownAsync(int delay = 0);
    Task<PowerResponse> RebootAsync(int delay = 0);
    Task<PowerResponse> ShutdownWithDialogAsync(int delay = 30, string message = "Компьютер будет выключен");
    Task<PowerResponse> RebootWithDialogAsync(int delay = 30, string message = "Компьютер будет перезагружен");
    Task<PowerResponse> CancelShutdownAsync();

    /// <summary>Запланированное действие, если есть.</summary>
    PendingPowerAction? GetPending();
}
