using PidSimulator.Core;
using PidSimulator.Core.Models;

namespace PidSimulator.Tests;

public class PressurePatternTests
{
    private static ProcessModel Model(ModelKind kind, double initial = 0)
    {
        var model = ModelCatalog.Create(kind, seed: 1);
        foreach (string key in new[] { "dead", "sens", "noise", "resp", "tau", "leak" })
            if (model.P.ContainsKey(key)) model.P[key] = 0;
        if (model.P.ContainsKey("char")) model.P["char"] = 0;
        if (model.P.ContainsKey("temp")) model.P["temp"] = 20;
        model.Reset(initial);
        return model;
    }

    [Theory]
    [InlineData(100)]
    [InlineData(600)]
    public void AirValve_RatedFlowIsCalibratedAtReferencePressureAnd20C(double reference)
    {
        Assert.Equal(1, AirValveFlow.Ratio(reference, 0, reference, 20), 12);
        Assert.Equal(0, AirValveFlow.Ratio(reference, reference, reference, 20));
        Assert.Equal(0, AirValveFlow.Ratio(reference, reference + 1, reference, 20));
    }

    [Fact]
    public void AirValve_ChokesUsingAbsolutePressure_ThenFallsSmoothlyAsPressuresEqualize()
    {
        Assert.Equal(1, AirValveFlow.Ratio(600, 200, 600, 20), 12);
        double subcritical = AirValveFlow.Ratio(600, 300, 600, 20);
        Assert.InRange(subcritical, 0, 0.999999);
        Assert.True(AirValveFlow.Ratio(600, 500, 600, 20) < subcritical);
        double criticalGauge = (600 + 101.325) * Math.Pow(2 / 2.4, 1.4 / 0.4) - 101.325;
        Assert.InRange(AirValveFlow.Ratio(600, criticalGauge + 0.001, 600, 20), 0.999999, 1);
    }

    [Fact]
    public void AirValve_NormalFlowScalesWithInverseSquareRootOfUpstreamTemperature()
    {
        // Doubling absolute temperature leaves the same valve area but reduces mass flow.
        Assert.Equal(1 / Math.Sqrt(2), AirValveFlow.Ratio(600, 0, 600, 313.15), 12);
        // Both flows are choked: upstream absolute pressure, not gauge pressure, sets their ratio.
        Assert.Equal(401.325 / 201.325, AirValveFlow.Ratio(300, 0, 100, 20), 12);
    }

    [Fact]
    public void SupplyValve_ConvertsRatedNormalFlowToTankPressure()
    {
        var model = Model(ModelKind.PressureSupplyValve);
        model.P["psupply"] = 600; model.P["qmax"] = 150; model.P["qout"] = 0; model.P["vol"] = 1;
        double pressure = model.Step(100, 0, 1, 1);
        double expected = 101.325 * 293.15 / 273.15 / 3600 * 150;
        Assert.Equal(expected, pressure, 8);
        model.Reset(0);
        Assert.Equal(0, model.Step(0, 0, 1, 1));
        model.Reset(0);
        Assert.Equal(expected / 2, model.Step(50, 0, 1, 1), 8);
    }

    [Fact]
    public void SupplyValve_DoesNotCrossSupplyPressure_EvenForTinyTankAndLargeStep()
    {
        var model = Model(ModelKind.PressureSupplyValve);
        model.P["vol"] = 1e-6; model.P["psupply"] = 600; model.P["qout"] = 0;
        double previous = 0;
        for (int i = 0; i < 5; i++)
        {
            double pressure = model.Step(100, 0, 1e6, (i + 1) * 1e6);
            Assert.InRange(pressure, previous, 600);
            previous = pressure;
        }
        Assert.Equal(600, model.X, 6);
        model.Reset(700);
        Assert.Equal(700, model.Step(100, 0, 1, 1)); // No reverse flow or artificial pressure reset.
    }

    [Fact]
    public void SupplyValve_ConsumptionAndSupplyPressureForceHaveExpectedDirections()
    {
        var normal = Model(ModelKind.PressureSupplyValve, 100);
        var consuming = Model(ModelKind.PressureSupplyValve, 100);
        Assert.True(normal.Step(100, 0, 1, 1) > consuming.Step(100, 10, 1, 1));
        var lowerSupply = Model(ModelKind.PressureSupplyValve, 100);
        lowerSupply.InputOverride = 50;
        Assert.True(lowerSupply.Step(100, 0, 1, 1) <= 100);
        lowerSupply.Reset(0);
        Assert.Equal(0, lowerSupply.Step(0, 1e6, 1000, 1000));
    }

    [Fact]
    public void ExhaustValve_RatedSupplyAndExhaustBalanceAt100Kpa()
    {
        var model = Model(ModelKind.PressureExhaustValve, 100);
        model.P["qsup"] = 150; model.P["qmax"] = 150;
        Assert.Equal(100, model.Step(100, 0, 100, 100), 9);
        Assert.True(model.Step(50, 0, 1, 101) > 100);
        model.P["qsup"] = 0;
        model.Reset(100);
        Assert.InRange(model.Step(100, 0, 1, 1), 0, 99.999999);
    }

    [Fact]
    public void ExhaustValve_ClosedTankObeysIdealGasLawTemperatureAndElapsedTime()
    {
        var coarse = Model(ModelKind.PressureExhaustValve);
        var fine = Model(ModelKind.PressureExhaustValve);
        var hotter = Model(ModelKind.PressureExhaustValve);
        foreach (var model in new[] { coarse, fine, hotter })
        {
            model.P["vol"] = 0.1; model.P["qsup"] = 6;
        }
        coarse.P["temp"] = 0; fine.P["temp"] = 0; hotter.P["temp"] = 273.15;
        coarse.Step(0, 0, 60, 60);
        hotter.Step(0, 0, 60, 60);
        for (int i = 0; i < 600; i++) fine.Step(0, 0, 0.1, (i + 1) * 0.1);
        Assert.Equal(101.325, coarse.X, 7);
        Assert.Equal(coarse.X, fine.X, 6);
        Assert.Equal(2 * coarse.X, hotter.X, 7);
    }

    [Fact]
    public void ExhaustValve_LargeStepApproachesEquilibriumWithoutOscillationOrVacuum()
    {
        var model = Model(ModelKind.PressureExhaustValve, 100);
        model.P["vol"] = 0.01; model.P["qmax"] = 150; model.P["qsup"] = 20;
        double previous = 100;
        for (int i = 0; i < 5; i++)
        {
            double pressure = model.Step(100, 0, 1e6, (i + 1) * 1e6);
            // The pressure root is solved to a relative tolerance of 1e-12.
            Assert.InRange(pressure, 0, previous + 1e-9);
            previous = pressure;
        }
        Assert.InRange(Math.Abs(20 - 150 * AirValveFlow.Ratio(model.X, 0, 100, 20)), 0, 1e-5);
        model.P["qsup"] = 0;
        Assert.InRange(model.Step(100, 0, 1e8, 1e8), 0, 1e-5);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 125)]
    [InlineData(100, 500)]
    public void PumpPressure_ClosedDischargeFollowsSquareOfSpeed(double speed, double expected)
    {
        var model = Model(ModelKind.PumpPressure);
        model.P["pmax"] = 500; model.P["qmax"] = 50; model.P["qout"] = 0;
        Assert.Equal(expected, model.Step(speed, 0, 0.1, 0.1), 10);
    }

    [Fact]
    public void PumpPressure_OperatingPointBalancesPumpAndDemandFlow()
    {
        var model = Model(ModelKind.PumpPressure);
        model.P["pmax"] = 500; model.P["qmax"] = 50; model.P["qout"] = 20;
        double pressure = model.Step(100, 0, 0.1, 0.1);
        Assert.Equal(2500d / 9, pressure, 9);
        Assert.Equal(50 * Math.Sqrt(1 - pressure / 500), 20 * Math.Sqrt(pressure / 100), 9);
        Assert.True(model.Step(100, 10, 0.1, 0.2) < pressure);
        model.InputOverride = 0;
        Assert.Equal(500, model.Step(100, 0, 0.1, 0.3), 9);
        model.P["qmax"] = 0;
        Assert.Equal(0, model.Step(100, 0, 0.1, 0.4));
    }

    [Fact]
    public void PumpPressure_ResponseIsIndependentOfStepSizeAndCannotOvershoot()
    {
        var coarse = Model(ModelKind.PumpPressure);
        var fine = Model(ModelKind.PumpPressure);
        coarse.P["tau"] = fine.P["tau"] = 2;
        coarse.P["qout"] = fine.P["qout"] = 0;
        double expected = coarse.P["pmax"] * (1 - Math.Exp(-1));
        coarse.Step(100, 0, 2, 2);
        for (int i = 0; i < 20; i++) fine.Step(100, 0, 0.1, (i + 1) * 0.1);
        Assert.Equal(expected, coarse.X, 9);
        Assert.Equal(coarse.X, fine.X, 9);
        Assert.InRange(coarse.Step(100, 0, 1e6, 1e6), expected, coarse.P["pmax"]);
    }
}
