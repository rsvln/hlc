// Models/FanProfile.cs
namespace HomeLabControlAgent.Models;

public class FanProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string SensorId { get; set; } = string.Empty;
    public string FanControllerId { get; set; } = string.Empty;
    
    public CurveType CurveType { get; set; } = CurveType.Linear;
    public List<CurvePoint> Points { get; set; } = new();
    public string? CustomFormula { get; set; }
    
    public int HysteresisUp { get; set; } = 2;
    public int HysteresisDown { get; set; } = 3;
    public int UpdateIntervalMs { get; set; } = 2000;
    
    public bool Enabled { get; set; } = false;
}

public enum CurveType
{
    Linear,
    Spline,
    Exponential,
    Custom
}