using PidSimulator.Core;
using PidSimulator.Core.Models;

namespace PidSimulator.Tests;

public class ModelPhysicsTests
{
    private static ProcessModel Model(ModelKind kind, double initial = 0)
    {
        var model = ModelCatalog.Create(kind, seed: 1);
        model.P["dead"] = 0;
        model.P["sens"] = 0;
        model.P["noise"] = 0;
        model.Reset(initial);
        return model;
    }

    [Fact]
    public void DeadTime_DelaysInitialInput_AndResetClearsItsHistory()
    {
        var model = Model(ModelKind.Flow);
        model.P["dead"] = 0.2; model.P["tau"] = 0;
        model.P["res"] = 0; model.P["pvar"] = 0;
        Assert.Equal(0, model.Step(100, 0, 0.1, 0.1));
        Assert.Equal(0, model.Step(100, 0, 0.1, 0.2));
        Assert.Equal(model.P["qmax"], model.Step(100, 0, 0.1, 0.3));

        model.Reset(0);
        for (int i = 0; i < 4; i++) Assert.Equal(0, model.Step(0, 0, 0.1, i * 0.1));
        Assert.Equal(0, model.Step(100, 0, 0.1, 0.5));
        Assert.Equal(0, model.Step(100, 0, 0.1, 0.6));
        Assert.Equal(model.P["qmax"], model.Step(100, 0, 0.1, 0.7));
    }

    [Theory]
    [InlineData(ModelKind.Flow, 0.001)]
    [InlineData(ModelKind.Flow, 1e-320)]
    [InlineData(ModelKind.Motor, 0.001)]
    [InlineData(ModelKind.Motor, 0)]
    public void VeryShortTimeConstant_DoesNotOvershootOrOscillate(ModelKind kind, double tau)
    {
        var model = Model(kind);
        model.P["tau"] = tau;
        double maximum;
        if (kind == ModelKind.Flow)
        {
            model.P["res"] = 0; model.P["pvar"] = 0;
            maximum = model.P["qmax"];
        }
        else
        {
            model.P["load"] = 0; model.P["acc"] = 1e8;
            maximum = model.P["nmax"];
        }
        for (int i = 0; i < 10; i++) Assert.InRange(model.Step(100, 0, 0.1, i * 0.1), 0, maximum);
        Assert.Equal(maximum, model.X, 8);
        Assert.InRange(model.Step(0, 0, 0.1, 1.1), 0, maximum);
    }

    [Fact]
    public void SensorTimeConstant_ReachesOneMinusExpMinusOne_AfterOneTimeConstant()
    {
        var model = Model(ModelKind.Flow);
        model.P["tau"] = 0; model.P["res"] = 0; model.P["pvar"] = 0;
        model.P["sens"] = 1;
        Assert.Equal(model.P["qmax"] * (1 - Math.Exp(-1)), model.Step(100, 0, 1, 1), 9);
    }

    [Fact]
    public void Flow_QuarterOfDifferentialPressure_ProducesHalfFlow()
    {
        var model = Model(ModelKind.Flow);
        model.P["tau"] = 0; model.P["res"] = 0; model.P["pvar"] = 0;
        model.InputOverride = 25;
        Assert.Equal(model.P["qmax"] / 2, model.Step(100, 0, 0.1, 0.1), 9);
        model.InputOverride = 100;
        Assert.Equal(model.P["qmax"] / 2, model.Step(100, 75, 0.1, 0.2), 9);
    }

    [Fact]
    public void Level_SmallTankApproachesNaturalDrainEquilibriumWithoutOscillation()
    {
        var model = Model(ModelKind.Level);
        model.P["area"] = 0.001; model.P["height"] = 1;
        model.P["valve"] = 0; model.P["qin"] = 5; model.P["kout"] = 1;
        double previous = 0;
        for (int i = 0; i < 10; i++)
        {
            model.Step(100, 0, 0.1, i * 0.1);
            Assert.InRange(model.X, previous, 25);
            previous = model.X;
        }
        Assert.Equal(25, model.X, 7);
    }

    [Fact]
    public void Pressure_NormalizedAirVolumeIncludesTankTemperature()
    {
        var model = Model(ModelKind.Pressure);
        model.P["vol"] = 0.1; model.P["qsup"] = 6; model.P["qout"] = 0;
        model.P["leak"] = 0; model.P["resp"] = 0; model.P["temp"] = 20;
        for (int i = 0; i < 600; i++) model.Step(100, 0, 0.1, i * 0.1);
        Assert.Equal(101.325 * 293.15 / 273.15, model.X, 7);
    }

    [Fact]
    public void Pressure_TinyTankWithLeak_ApproachesEquilibriumWithoutOvershoot()
    {
        var model = Model(ModelKind.Pressure);
        model.P["vol"] = 0.0001; model.P["qsup"] = 6; model.P["qout"] = 0;
        model.P["leak"] = 2; model.P["resp"] = 0; model.P["temp"] = 20;
        for (int i = 0; i < 10; i++) Assert.InRange(model.Step(100, 0, 10, i * 10), 0, 300);
        Assert.Equal(300, model.X, 8);
    }

    [Fact]
    public void Heater_PassiveCooling_DoesNotCrossAmbientWithSmallHeatCapacity()
    {
        var model = Model(ModelKind.Heater, 100);
        model.P["cap"] = 0.001; model.P["hloss"] = 1; model.P["amb"] = 25;
        Assert.Equal(25, model.Step(0, 0, 10, 10), 8);
    }

    private static ProcessModel Steam(double initial)
    {
        var model = Model(ModelKind.Steam, initial);
        model.P["ts"] = 120; model.P["ua"] = 1; model.P["pmax"] = 40;
        model.P["cap"] = 2; model.P["hloss"] = 0.2;
        model.P["amb"] = 25; model.P["load"] = 0;
        return model;
    }

    [Fact]
    public void Steam_CrossingPowerAndTransferLimits_IsIndependentOfStepSize()
    {
        var coarse = Steam(25);
        var fine = Steam(25);
        coarse.Step(100, 0, 10, 10);
        for (int i = 0; i < 100; i++) fine.Step(100, 0, 0.1, i * 0.1);
        Assert.Equal(fine.X, coarse.X, 8);
        Assert.InRange(coarse.X, 80, 120);
    }

    [Fact]
    public void Steam_DoesNotHeatAboveSteamTemperature_OrCoolHotterWater()
    {
        var model = Steam(25);
        model.P["cap"] = 0.001; model.P["hloss"] = 0;
        Assert.Equal(120, model.Step(100, 0, 100, 100), 8);
        model.Reset(150);
        Assert.Equal(150, model.Step(100, 0, 100, 100), 8);
    }

    [Fact]
    public void Chiller_InitialTemperatureBelowLimit_DoesNotJumpToLimit()
    {
        var model = Model(ModelKind.Chiller, 1);
        model.P["tmin"] = 3; model.P["resp"] = 0; model.P["cap"] = 1;
        model.P["hloss"] = 0; model.P["load"] = 0;
        Assert.Equal(1, model.Step(100, 0, 0.1, 0.1), 8);
        model.P["load"] = 1;
        Assert.Equal(1.1, model.Step(100, 0, 0.1, 0.2), 8);
        Assert.Equal(3, model.Step(100, 0, 100, 100), 8);
    }
}
