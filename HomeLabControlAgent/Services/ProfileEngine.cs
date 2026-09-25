// Services/ProfileEngine.cs
namespace HomeLabControlAgent.Services;

using HomeLabControlAgent.Models;

public class ProfileEngine : BackgroundService
{
    /// <summary>Сколько циклов подряд датчик может отсутствовать, прежде чем включить fail-safe.</summary>
    private const int SensorMissingThreshold = 3;

    private readonly ILogger<ProfileEngine> _logger;
    private readonly IProfileStorage _profileStorage;
    private readonly IHardwareMonitor _hardwareMonitor;
    private readonly CurveCalculator _curveCalculator;
    private readonly Dictionary<string, ProfileState> _profileStates = new();

    public ProfileEngine(
        ILogger<ProfileEngine> logger,
        IProfileStorage profileStorage,
        IHardwareMonitor hardwareMonitor,
        CurveCalculator curveCalculator)
    {
        _logger = logger;
        _profileStorage = profileStorage;
        _hardwareMonitor = hardwareMonitor;
        _curveCalculator = curveCalculator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProfileEngine started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessProfilesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ProfileEngine main loop");
            }

            try
            {
                await Task.Delay(500, stoppingToken); // Минимальная задержка между итерациями
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("ProfileEngine stopped");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // Агент останавливается — не оставляем вентиляторы на последнем ручном значении
        foreach (var fanId in _profileStates.Values.Select(s => s.FanControllerId).Distinct())
            RestoreAuto(fanId, "agent stopping");

        _profileStates.Clear();
    }

    private async Task ProcessProfilesAsync(CancellationToken cancellationToken)
    {
        var profiles = await _profileStorage.GetAllAsync();
        var enabledProfiles = profiles.Where(p => p.Enabled).ToList();

        // Профили, которые выключили/удалили/перевесили на другой вентилятор — отдаём вентилятор BIOS
        var activeProfileIds = enabledProfiles.Select(p => p.Id).ToHashSet();
        var activeFanIds = enabledProfiles.Select(p => p.FanControllerId).ToHashSet();
        foreach (var (id, state) in _profileStates.ToList())
        {
            var profile = enabledProfiles.FirstOrDefault(p => p.Id == id);
            if (profile != null && profile.FanControllerId == state.FanControllerId)
                continue;

            _profileStates.Remove(id);
            if (!activeFanIds.Contains(state.FanControllerId))
                RestoreAuto(state.FanControllerId, $"profile {id} disabled");
        }

        if (enabledProfiles.Count == 0)
            return;

        // Обновляем данные с железа
        _hardwareMonitor.Update();

        foreach (var profile in enabledProfiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            ProcessProfile(profile);
        }
    }

    private void ProcessProfile(FanProfile profile)
    {
        try
        {
            // Получаем или создаём состояние профиля
            if (!_profileStates.TryGetValue(profile.Id, out var state))
            {
                state = new ProfileState { FanControllerId = profile.FanControllerId };
                _profileStates[profile.Id] = state;
            }

            // Проверяем нужно ли обновлять (по UpdateIntervalMs)
            var now = DateTime.UtcNow;
            if ((now - state.LastUpdate).TotalMilliseconds < profile.UpdateIntervalMs)
                return;

            state.LastUpdate = now;

            // Читаем температуру
            var sensor = _hardwareMonitor.GetSensor(profile.SensorId);
            if (sensor == null)
            {
                state.MissingSensorCount++;
                _logger.LogWarning("Sensor not found for profile {ProfileId}: {SensorId} ({Count} in a row)",
                    profile.Id, profile.SensorId, state.MissingSensorCount);

                // Не знаем температуру — крутим на полную, а не оставляем как было
                if (state.MissingSensorCount >= SensorMissingThreshold && state.LastSpeed != CurveCalculator.FailsafeSpeed)
                {
                    if (_hardwareMonitor.SetFanSpeed(profile.FanControllerId, CurveCalculator.FailsafeSpeed))
                    {
                        state.LastSpeed = CurveCalculator.FailsafeSpeed;
                        state.LastTemperature = null;
                    }
                }
                return;
            }

            state.MissingSensorCount = 0;
            var temperature = sensor.Value;

            // Вычисляем обороты по кривой
            var targetSpeed = _curveCalculator.CalculateSpeed(temperature, profile);

            // Применяем гистерезис
            var finalSpeed = ApplyHysteresis(targetSpeed, temperature, profile, state);

            // Читаем текущую ФАКТИЧЕСКУЮ скорость вентилятора
            var fan = _hardwareMonitor.GetFan(profile.FanControllerId);
            if (fan == null)
            {
                _logger.LogWarning("Fan not found for profile {ProfileId}: {FanId}",
                    profile.Id, profile.FanControllerId);
                return;
            }

            var deviation = Math.Abs(fan.CurrentSpeedPercent - finalSpeed);

            // Устанавливаем обороты если:
            // 1. Целевая скорость изменилась ИЛИ
            // 2. Фактическая скорость отличается от целевой больше чем на 5% (материнка перехватила)
            if (state.LastSpeed != finalSpeed || deviation > 5)
            {
                if (_hardwareMonitor.SetFanSpeed(profile.FanControllerId, finalSpeed))
                {
                    state.LastSpeed = finalSpeed;
                    state.LastTemperature = temperature;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing profile {ProfileId}", profile.Id);
        }
    }

    private int ApplyHysteresis(int targetSpeed, double currentTemp, FanProfile profile, ProfileState state)
    {
        // Первый запуск - сразу применяем целевое значение
        if (!state.LastTemperature.HasValue)
            return targetSpeed;

        var tempDiff = currentTemp - state.LastTemperature.Value;
        var speedDiff = targetSpeed - state.LastSpeed;

        // Температура растёт
        if (tempDiff > 0 && speedDiff > 0)
        {
            // Если рост температуры меньше HysteresisUp градусов - не увеличиваем обороты
            if (tempDiff < profile.HysteresisUp)
                return state.LastSpeed;
        }
        // Температура падает
        else if (tempDiff < 0 && speedDiff < 0)
        {
            // Если падение температуры меньше HysteresisDown градусов - не уменьшаем обороты
            if (Math.Abs(tempDiff) < profile.HysteresisDown)
                return state.LastSpeed;
        }

        return targetSpeed;
    }

    private void RestoreAuto(string fanId, string reason)
    {
        try
        {
            if (_hardwareMonitor.RestoreAutoMode(fanId))
                _logger.LogInformation("Fan {FanId} returned to automatic control ({Reason})", fanId, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore automatic control for fan {FanId}", fanId);
        }
    }

    private class ProfileState
    {
        public string FanControllerId { get; init; } = string.Empty;
        public int LastSpeed { get; set; } = -1;
        public double? LastTemperature { get; set; }
        public DateTime LastUpdate { get; set; } = DateTime.MinValue;
        public int MissingSensorCount { get; set; }
    }
}
