// Models/AgentApiModels.cs — DTO ответов HomeLabControlAgent

using System.Text.Json.Serialization;

namespace HomeLabControl.Models;


public class SensorInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Type { get; set; }
    public double Value { get; set; }
    public string Unit { get; set; } = string.Empty;
}

public class FanInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("currentSpeed")]
    public int CurrentSpeed { get; set; }

    [JsonPropertyName("currentSpeedPercent")]
    public int CurrentSpeedPercent { get; set; }

    [JsonPropertyName("minSpeed")]
    public int MinSpeed { get; set; }

    [JsonPropertyName("maxSpeed")]
    public int MaxSpeed { get; set; }

    [JsonPropertyName("supportsControl")]
    public bool SupportsControl { get; set; }

    [JsonPropertyName("controlType")]
    public int ControlType { get; set; }

    [JsonPropertyName("isAutoMode")]
    public bool IsAutoMode { get; set; }
}

public class ProfileInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SensorId { get; set; } = string.Empty;
    public string FanControllerId { get; set; } = string.Empty;
    public int CurveType { get; set; }
    public List<CurvePoint> Points { get; set; } = new();
    public string? CustomFormula { get; set; }
    public int HysteresisUp { get; set; }
    public int HysteresisDown { get; set; }
    public int UpdateIntervalMs { get; set; }
    public bool Enabled { get; set; }
}

/// <summary>Состояние включённого профиля на агенте (GET /api/profiles/status, агент 2.0.7+).</summary>
public class ProfileStatusInfo
{
    public const string Ok = "ok";
    public const string Pending = "pending";
    public const string SensorMissing = "sensorMissing";
    public const string Failsafe = "failsafe";
    public const string FanMissing = "fanMissing";

    public string ProfileId { get; set; } = string.Empty;
    public string State { get; set; } = Pending;
    public string SensorId { get; set; } = string.Empty;
    public string FanControllerId { get; set; } = string.Empty;
    public double? Temperature { get; set; }
    public int? Speed { get; set; }
    public DateTime Since { get; set; }

    /// <summary>Проблема, о которой стоит сказать: вентилятор не по кривой.</summary>
    public bool IsProblem => State is Failsafe or FanMissing or SensorMissing;
}

public class CurvePoint
{
    public double Temperature { get; set; }
    public int Speed { get; set; }
}