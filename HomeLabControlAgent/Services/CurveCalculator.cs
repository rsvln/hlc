// Services/CurveCalculator.cs
namespace HomeLabControlAgent.Services;

using HomeLabControlAgent.Models;
using MathNet.Numerics.Interpolation;
using NCalc;

public class CurveCalculator
{
    /// <summary>
    /// Скорость при ошибке расчёта: лучше шумно, чем горячо.
    /// Раньше при ошибке возвращался 0% — вентилятор останавливался.
    /// </summary>
    public const int FailsafeSpeed = 100;

    private readonly ILogger<CurveCalculator> _logger;

    public CurveCalculator(ILogger<CurveCalculator> logger)
    {
        _logger = logger;
    }

    public int CalculateSpeed(double temperature, FanProfile profile)
    {
        try
        {
            // Custom-профиль задаётся только формулой, точки у него могут отсутствовать
            if (profile.CurveType == CurveType.Custom)
                return CalculateCustom(temperature, profile.CustomFormula);

            if (profile.Points.Count == 0)
                return FailsafeSpeed;

            var speed = profile.CurveType switch
            {
                CurveType.Linear => CalculateLinear(temperature, profile.Points),
                CurveType.Spline => CalculateSpline(temperature, profile.Points),
                CurveType.Exponential => CalculateExponential(temperature, profile.Points),
                _ => FailsafeSpeed
            };

            return Math.Clamp(speed, 0, 100);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to calculate speed for profile {ProfileId}", profile.Id);
            return FailsafeSpeed;
        }
    }

    private int CalculateLinear(double temp, List<CurvePoint> points)
    {
        var sorted = points.OrderBy(p => p.Temperature).ToList();

        // Ниже минимума
        if (temp <= sorted.First().Temperature)
            return sorted.First().Speed;

        // Выше максимума
        if (temp >= sorted.Last().Temperature)
            return sorted.Last().Speed;

        // Интерполяция между точками
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var p1 = sorted[i];
            var p2 = sorted[i + 1];

            if (temp >= p1.Temperature && temp <= p2.Temperature)
            {
                if (p2.Temperature == p1.Temperature)
                    return Math.Max(p1.Speed, p2.Speed);

                var ratio = (temp - p1.Temperature) / (p2.Temperature - p1.Temperature);
                var speed = p1.Speed + ratio * (p2.Speed - p1.Speed);
                return (int)Math.Round(speed);
            }
        }

        return sorted.Last().Speed;
    }

    private int CalculateSpline(double temp, List<CurvePoint> points)
    {
        if (points.Count < 3)
            return CalculateLinear(temp, points);

        var sorted = points.OrderBy(p => p.Temperature).ToList();

        // За пределами точек сплайн экстраполирует непредсказуемо — держим крайние значения
        if (temp <= sorted.First().Temperature)
            return sorted.First().Speed;
        if (temp >= sorted.Last().Temperature)
            return sorted.Last().Speed;

        var temps = sorted.Select(p => p.Temperature).ToArray();
        var speeds = sorted.Select(p => (double)p.Speed).ToArray();

        var spline = CubicSpline.InterpolateNatural(temps, speeds);
        var result = spline.Interpolate(temp);

        return (int)Math.Round(Math.Clamp(result, 0, 100));
    }

    private int CalculateExponential(double temp, List<CurvePoint> points)
    {
        if (points.Count < 2)
            return points.FirstOrDefault()?.Speed ?? FailsafeSpeed;

        var sorted = points.OrderBy(p => p.Temperature).ToList();
        var minTemp = sorted.First().Temperature;
        var maxTemp = sorted.Last().Temperature;
        var minSpeed = sorted.First().Speed;
        var maxSpeed = sorted.Last().Speed;

        if (temp <= minTemp) return minSpeed;
        if (temp >= maxTemp) return maxSpeed;

        // Степенная кривая: медленный рост в начале, быстрый — ближе к maxTemp
        var normalized = (temp - minTemp) / (maxTemp - minTemp);
        var expValue = Math.Pow(normalized, 1.5);
        var speed = minSpeed + expValue * (maxSpeed - minSpeed);

        return (int)Math.Round(Math.Clamp(speed, 0, 100));
    }

    private int CalculateCustom(double temp, string? formula)
    {
        if (string.IsNullOrWhiteSpace(formula))
            return FailsafeSpeed;

        try
        {
            var expr = new Expression(formula);
            expr.Parameters["temp"] = temp;
            expr.Parameters["x"] = temp; // Алиас

            var result = Convert.ToDouble(expr.Evaluate());
            if (double.IsNaN(result) || double.IsInfinity(result))
                return FailsafeSpeed;

            return (int)Math.Round(Math.Clamp(result, 0, 100));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evaluate custom formula: {Formula}", formula);
            return FailsafeSpeed;
        }
    }
}
