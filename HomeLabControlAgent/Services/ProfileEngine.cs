// Services/ProfileEngine.cs
namespace HomeLabControlAgent.Services;

using System.Collections.Concurrent;
using HomeLabControlAgent.Models;

public class ProfileEngine : BackgroundService
{
    /// <summary>Сколько циклов подряд датчик может отсутствовать, прежде чем включить fail-safe.</summary>
    private const int SensorMissingThreshold = 3;

    private readonly ILogger<ProfileEngine> _logger;
    private readonly IProfileStorage _profileStorage;
    private readonly IHardwareMonitor _hardwareMonitor;
    private readonly CurveCalculator _curveCalculator;
    // Цикл профилей пишет, GET /api/profiles/status читает из другого потока
    private readonly ConcurrentDictionary<string, ProfileState> _profileStates = new();

    /// <summary>
    /// Переустанавливать скорость не реже этого интервала, даже если она не менялась.
    /// Перехват вентилятора BIOS/SMM на Windows не виден (LibreHardwareMonitor отдаёт записанное нами значение),
    /// поэтому единственная надёжная защита — периодически записывать заново. 0 — выключено.
    /// </summary>
    private readonly TimeSpan _reapplyInterval;

    public ProfileEngine(
        ILogger<ProfileEngine> logger,
        IProfileStorage profileStorage,
        IHardwareMonitor hardwareMonitor,
        CurveCalculator curveCalculator,
        IConfiguration configuration)
    {
        _reapplyInterval = TimeSpan.FromSeconds(configuration.GetValue("FanControl:ReapplyIntervalSeconds", 30));
        _logger = logger;
        _profileStorage = profileStorage;
        _hardwareMonitor = hardwareMonitor;
        _curveCalculator = curveCalculator;
    }

    /// <summary>Состояние включённых профилей: работает, датчик пропал, fail-safe, нет вентилятора.</summary>
    public IReadOnlyList<ProfileStatus> GetStatuses()
        => _profileStates.Select(kvp => new ProfileStatus
        {
            ProfileId = kvp.Key,
            State = kvp.Value.State,
            SensorId = kvp.Value.SensorId,
            FanControllerId = kvp.Value.FanControllerId,
            Temperature = kvp.Value.CurrentTemperature,
            Speed = kvp.Value.LastSpeed >= 0 ? kvp.Value.LastSpeed : null,
            Since = kvp.Value.StateSince
        }).ToList();

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

            _profileStates.TryRemove(id, out _);
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
            state.SensorId = profile.SensorId;

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
                state.CurrentTemperature = null;

                // Не знаем температуру — крутим на полную, а не оставляем как было
                if (state.MissingSensorCount >= SensorMissingThreshold)
                {
                    if (state.LastSpeed != CurveCalculator.FailsafeSpeed &&
                        _hardwareMonitor.SetFanSpeed(profile.FanControllerId, CurveCalculator.FailsafeSpeed))
                    {
                        state.LastSpeed = CurveCalculator.FailsafeSpeed;
                        state.LastTemperature = null;
                    }
                    SetState(profile, state, ProfileStates.Failsafe);
                }
                else
                {
                    SetState(profile, state, ProfileStates.SensorMissing);
                }
                return;
            }

            state.MissingSensorCount = 0;
            var temperature = sensor.Value;
            state.CurrentTemperature = temperature;

            // Вычисляем обороты по кривой
            var targetSpeed = _curveCalculator.CalculateSpeed(temperature, profile);

            // Применяем гистерезис
            var finalSpeed = ApplyHysteresis(targetSpeed, temperature, profile, state);

            // Читаем текущую ФАКТИЧЕСКУЮ скорость вентилятора
            var fan = _hardwareMonitor.GetFan(profile.FanControllerId);
            if (fan == null)
            {
                SetState(profile, state, ProfileStates.FanMissing);
                return;
            }

            SetState(profile, state, ProfileStates.Ok);

            var deviation = Math.Abs(fan.CurrentSpeedPercent - finalSpeed);

            // Устанавливаем обороты если:
            // 1. Целевая скорость изменилась ИЛИ
            // 2. Фактическая скорость отличается от целевой больше чем на 5% (материнка перехватила) ИЛИ
            // 3. Давно не записывали (перехват, который не виден по показаниям)
            var reapplyDue = _reapplyInterval > TimeSpan.Zero && now - state.LastApplied >= _reapplyInterval;
            if (state.LastSpeed != finalSpeed || deviation > 5 || reapplyDue)
            {
                if (_hardwareMonitor.SetFanSpeed(profile.FanControllerId, finalSpeed))
                {
                    if (state.LastSpeed != finalSpeed)
                        state.LastTemperature = temperature;
                    state.LastSpeed = finalSpeed;
                    state.LastApplied = now;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing profile {ProfileId}", profile.Id);
        }
    }

    /// <summary>Смена состояния профиля — в лог один раз, а не каждый цикл.</summary>
    private void SetState(FanProfile profile, ProfileState state, string newState)
    {
        if (state.State == newState)
            return;

        var previous = state.State;
        state.State = newState;
        state.StateSince = DateTime.UtcNow;

        switch (newState)
        {
            case ProfileStates.SensorMissing:
                _logger.LogWarning("Profile {Profile}: sensor {SensorId} not found — was it renamed after an update? Pick it again in the profile",
                    profile.Name, profile.SensorId);
                break;
            case ProfileStates.Failsafe:
                _logger.LogWarning("Profile {Profile}: sensor {SensorId} still not found — fan {FanId} set to {Speed}% (fail-safe)",
                    profile.Name, profile.SensorId, profile.FanControllerId, CurveCalculator.FailsafeSpeed);
                break;
            case ProfileStates.FanMissing:
                _logger.LogWarning("Profile {Profile}: fan {FanId} not found", profile.Name, profile.FanControllerId);
                break;
            case ProfileStates.Ok when previous != ProfileStates.Pending:
                _logger.LogInformation("Profile {Profile}: working again (was {Previous})", profile.Name, previous);
                break;
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
        public string SensorId { get; set; } = string.Empty;
        public string State { get; set; } = ProfileStates.Pending;
        public DateTime StateSince { get; set; } = DateTime.UtcNow;
        public double? CurrentTemperature { get; set; }
        public int LastSpeed { get; set; } = -1;
        public double? LastTemperature { get; set; }
        public DateTime LastUpdate { get; set; } = DateTime.MinValue;
        public DateTime LastApplied { get; set; } = DateTime.MinValue;
        public int MissingSensorCount { get; set; }
    }
}
