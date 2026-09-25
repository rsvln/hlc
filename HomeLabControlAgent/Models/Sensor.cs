// Models/Sensor.cs
namespace HomeLabControlAgent.Models;

public class Sensor
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public SensorType Type { get; set; }
    public double Value { get; set; }
    public string Unit { get; set; } = string.Empty;
}

public enum SensorType
{
    Temperature,
    Fan,
    Voltage,
    Power,
    Clock
}