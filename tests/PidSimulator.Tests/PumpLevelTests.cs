using PidSimulator.Core;
using PidSimulator.Core.Models;

namespace PidSimulator.Tests;

public class PumpLevelTests
{
    private static ProcessModel Model(ModelKind kind, double initial = 50)
    {
        var model = ModelCatalog.Create(kind, seed: 1);
        model.P["dead"] = 0; model.P["sens"] = 0; model.P["noise"] = 0;
        model.P["height"] = 1000; model.P["area"] = 1;
        model.P["qmax"] = 3; model.P["hmax"] = 30; model.P["hstatic"] = 0;
        model.P["resp"] = 0;
        model.P[kind == ModelKind.LevelPumpIn ? "kout" : "qin"] = 0;
        model.Reset(initial);
        return model;
    }

    [Theory]
    [InlineData(ModelKind.LevelPumpIn)]
    [InlineData(ModelKind.LevelPumpOut)]
    public void FullSpeedFlowAtReferenceLevel_EqualsSpecifiedActualFlow(ModelKind kind)
    {
        var model = Model(kind);
        model.P["hstatic"] = 10;
        if (kind == ModelKind.LevelPumpIn) model.P["kout"] = 3 / Math.Sqrt(50);
        else model.P["qin"] = 3;
        Assert.Equal(50, model.Step(100, 0, 60, 60), 9);
    }

    [Theory]
    [InlineData(ModelKind.LevelPumpIn)]
    [InlineData(ModelKind.LevelPumpOut)]
    public void InsufficientSpeedCannotOvercomeStaticHead(ModelKind kind)
    {
        var model = Model(kind);
        model.P["hstatic"] = 10;
        Assert.Equal(50, model.Step(50, 0, 60, 60), 9);
        model.Step(80, 0, 60, 120);
        Assert.True(kind == ModelKind.LevelPumpIn ? model.X > 50 : model.X < 50);
    }

    [Theory]
    [InlineData(ModelKind.LevelPumpIn)]
    [InlineData(ModelKind.LevelPumpOut)]
    public void StoppedPumpHasNoReverseOrSiphonFlow(ModelKind kind)
    {
        var model = Model(kind, 80);
        Assert.Equal(80, model.Step(0, 0, 600, 600), 9);
    }

    [Fact]
    public void SpeedResponseUsesTimeConstant_AndResetRemovesRotatingState()
    {
        var model = Model(ModelKind.LevelPumpOut);
        model.P["resp"] = 1;
        double speed = 1 - Math.Exp(-1);
        double expected = 50 - 3 * speed / 3600 * 100;
        Assert.Equal(expected, model.Step(100, 0, 1, 1), 9);
        model.Reset(50);
        Assert.Equal(50, model.Step(0, 0, 1, 1), 9);
    }

    [Fact]
    public void ZeroStaticHeadDrain_ConservesWaterAtHalfSpeed_WithDifferentTankAreas()
    {
        var small = Model(ModelKind.LevelPumpOut);
        var large = Model(ModelKind.LevelPumpOut);
        large.P["area"] = 2;
        Assert.Equal(47.5, small.Step(50, 0, 60, 60), 9); // 25 L drawn from a 1000 L tank.
        Assert.Equal(48.75, large.Step(50, 0, 60, 60), 9);
    }

    [Fact]
    public void FillingFlowAccountsForWaterHeight_AndSatisfiesImplicitWaterBalance()
    {
        var model = Model(ModelKind.LevelPumpIn, 20);
        model.P["hstatic"] = 2;
        model.P["kout"] = 0.1;
        model.Step(80, 0.2, 60, 60);
        double head = 2 + model.X / 100;
        double flow = 3 * Math.Sqrt((0.8 * 0.8 - head / 30) / (1 - 2.5 / 30));
        double expected = 20 + (flow - 0.1 * Math.Sqrt(model.X) - 0.2) / 60 * 100;
        Assert.Equal(expected, model.X, 9);
    }

    [Theory]
    [InlineData(ModelKind.LevelPumpIn)]
    [InlineData(ModelKind.LevelPumpOut)]
    public void TinyTankApproachesItsEquilibriumWithoutOvershooting(ModelKind kind)
    {
        var model = Model(kind, 20);
        model.P["area"] = 0.000001;
        model.P["hstatic"] = 10;
        if (kind == ModelKind.LevelPumpIn) model.P["kout"] = 3 / Math.Sqrt(50);
        else model.P["qin"] = 3;
        double previous = model.X;
        for (int i = 0; i < 10; i++)
        {
            model.Step(100, 0, 1, i);
            Assert.InRange(model.X, previous - 1e-10, 50 + 1e-10);
            previous = model.X;
        }
        Assert.Equal(50, model.X, 7);
    }

    [Theory]
    [InlineData(ModelKind.LevelPumpIn, 100)]
    [InlineData(ModelKind.LevelPumpOut, 0)]
    public void TankBoundariesRemainFiniteWithVeryLargeFlow(ModelKind kind, double expected)
    {
        var model = Model(kind);
        model.P["qmax"] = 1e6;
        Assert.Equal(expected, model.Step(100, 0, 100, 100));
    }

    [Fact]
    public void ExternalFlowAndDisturbanceUseTheSelectedSide()
    {
        var fill = Model(ModelKind.LevelPumpIn);
        fill.P["kout"] = 0.1;
        fill.InputOverride = 0; // Close the separate outlet valve.
        Assert.Equal(50, fill.Step(0, 0, 60, 60), 9);
        Assert.Equal(49, fill.Step(0, 0.6, 60, 120), 9);

        var drain = Model(ModelKind.LevelPumpOut);
        drain.InputOverride = 0.6;
        Assert.Equal(52, drain.Step(0, 0.6, 60, 60), 9);
    }
}
