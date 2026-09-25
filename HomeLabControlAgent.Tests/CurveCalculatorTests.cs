using HomeLabControlAgent.Models;
using HomeLabControlAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeLabControlAgent.Tests;

public class CurveCalculatorTests
{
    private readonly CurveCalculator _calc = new(NullLogger<CurveCalculator>.Instance);

    private static FanProfile Profile(CurveType type, params (double T, int S)[] points) => new()
    {
        CurveType = type,
        Points = points.Select(p => new CurvePoint { Temperature = p.T, Speed = p.S }).ToList()
    };

    [Theory]
    [InlineData(20, 30)]   // ниже первой точки — первая скорость
    [InlineData(40, 30)]
    [InlineData(55, 65)]   // середина между 40/30 и 70/100
    [InlineData(70, 100)]
    [InlineData(90, 100)]  // выше последней — последняя
    public void Linear_interpolates_and_clamps_to_edges(double temp, int expected)
        => Assert.Equal(expected, _calc.CalculateSpeed(temp, Profile(CurveType.Linear, (70, 100), (40, 30))));

    [Fact]
    public void Spline_stays_within_edges_outside_points()
    {
        var profile = Profile(CurveType.Spline, (30, 20), (50, 40), (70, 100));
        Assert.Equal(20, _calc.CalculateSpeed(10, profile));
        Assert.Equal(100, _calc.CalculateSpeed(95, profile));
        Assert.InRange(_calc.CalculateSpeed(60, profile), 40, 100);
    }

    [Fact]
    public void Exponential_grows_slower_than_linear_at_start()
    {
        var exp = _calc.CalculateSpeed(50, Profile(CurveType.Exponential, (40, 20), (80, 100)));
        var lin = _calc.CalculateSpeed(50, Profile(CurveType.Linear, (40, 20), (80, 100)));
        Assert.True(exp < lin, $"exp {exp} should be below linear {lin}");
    }

    [Theory]
    [InlineData("temp * 2 - 40", 50, 60)]
    [InlineData("x * 2 - 40", 50, 60)]          // алиас x
    [InlineData("Max(30, temp - 10)", 35, 30)]  // функции NCalc
    [InlineData("if(temp > 60, 100, 40)", 70, 100)]
    [InlineData("temp * 10", 50, 100)]          // ограничение сверху
    [InlineData("temp - 100", 50, 0)]           // ограничение снизу
    public void Custom_formulas_work_without_points(string formula, double temp, int expected)
    {
        // Раньше Custom-профиль без точек всегда давал 0%
        var profile = new FanProfile { CurveType = CurveType.Custom, CustomFormula = formula };
        Assert.Equal(expected, _calc.CalculateSpeed(temp, profile));
    }

    [Theory]
    [InlineData("temp +")]        // синтаксическая ошибка
    [InlineData("unknown_var")]   // неизвестный параметр
    [InlineData("1 / 0")]         // бесконечность
    [InlineData("")]
    public void Custom_formula_errors_fall_back_to_full_speed(string formula)
    {
        var profile = new FanProfile { CurveType = CurveType.Custom, CustomFormula = formula };
        Assert.Equal(CurveCalculator.FailsafeSpeed, _calc.CalculateSpeed(50, profile));
    }

    [Fact]
    public void Profile_without_points_falls_back_to_full_speed()
        => Assert.Equal(CurveCalculator.FailsafeSpeed, _calc.CalculateSpeed(50, Profile(CurveType.Linear)));
}
