// Models/FanController.cs
namespace HomeLabControlAgent.Models;

public class FanController
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int CurrentSpeed { get; set; } // RPM
    public int CurrentSpeedPercent { get; set; } // 0-100
    public int MinSpeed { get; set; } = 0;
    public int MaxSpeed { get; set; } = 100;
    public bool SupportsControl { get; set; } = true;
    public ControlType ControlType { get; set; }
    public bool IsAutoMode { get; set; }
}

public enum ControlType
{
    PWM,
    DC,
    Unknown
}