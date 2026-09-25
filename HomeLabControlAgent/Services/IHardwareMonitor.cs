// Services/IHardwareMonitor.cs
namespace HomeLabControlAgent.Services;

using HomeLabControlAgent.Models;

public interface IHardwareMonitor : IDisposable
{
    List<Sensor> GetSensors();
    List<FanController> GetFans();
    Sensor? GetSensor(string sensorId);
    FanController? GetFan(string fanId);
    bool SetFanSpeed(string fanId, int speedPercent);

    /// <summary>Вернуть управление вентилятором BIOS/драйверу (отменить ручной PWM).</summary>
    bool RestoreAutoMode(string fanId);

    void Update();
}
