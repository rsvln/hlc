// Services/ISmartMonitorService.cs
namespace HomeLabControlAgent.Services;

using HomeLabControlAgent.Models;

public interface ISmartMonitorService
{
    /// <summary>Получить список всех дисков с полными SMART данными</summary>
    Task<List<SmartDisk>> GetAllDisksAsync();

    /// <summary>Получить данные конкретного диска по Id</summary>
    Task<SmartDisk?> GetDiskAsync(string diskId);

    /// <summary>Принудительно пересканировать все диски</summary>
    Task RefreshAsync();
}
