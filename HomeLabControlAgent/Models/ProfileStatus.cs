// Models/ProfileStatus.cs
namespace HomeLabControlAgent.Models;

/// <summary>Что сейчас происходит с включённым профилем (GET /api/profiles/status).</summary>
public static class ProfileStates
{
    /// <summary>Профиль включён, но ещё не обрабатывался.</summary>
    public const string Pending = "pending";

    /// <summary>Работает: температура читается, скорость по кривой.</summary>
    public const string Ok = "ok";

    /// <summary>Датчик не найден, ещё не набралось циклов для fail-safe — скорость прежняя.</summary>
    public const string SensorMissing = "sensorMissing";

    /// <summary>Датчик не найден несколько циклов подряд — вентилятор на 100%.</summary>
    public const string Failsafe = "failsafe";

    /// <summary>Вентилятор профиля не найден — управлять нечем.</summary>
    public const string FanMissing = "fanMissing";
}

public class ProfileStatus
{
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>См. <see cref="ProfileStates"/>.</summary>
    public string State { get; set; } = ProfileStates.Pending;

    public string SensorId { get; set; } = string.Empty;
    public string FanControllerId { get; set; } = string.Empty;

    /// <summary>Последняя прочитанная температура (null — датчик не читается).</summary>
    public double? Temperature { get; set; }

    /// <summary>Последняя записанная скорость, % (null — ещё не записывали).</summary>
    public int? Speed { get; set; }

    /// <summary>С какого момента (UTC) профиль в этом состоянии.</summary>
    public DateTime Since { get; set; }
}
