// Models/PowerOperation.cs
namespace HomeLabControlAgent.Models;

public enum PowerAction
{
    Shutdown,
    Reboot
}

public class PendingPowerAction
{
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<PowerAction>))]
    public PowerAction Action { get; set; }
    public DateTime ScheduledAt { get; set; }
    public DateTime ExecuteAt { get; set; }
    public int SecondsLeft => Math.Max(0, (int)Math.Ceiling((ExecuteAt - DateTime.UtcNow).TotalSeconds));
}

public class PowerResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Error { get; set; }
}
