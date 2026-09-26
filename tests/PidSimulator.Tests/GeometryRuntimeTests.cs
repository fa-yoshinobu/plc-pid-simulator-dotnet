using PidSimulator.Core;
using PidSimulator.Core.Models;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public sealed class GeometryRuntimeTests
{
    private static ControlTarget Level(string unit = "mm", double height = 2000)
    {
        var config = TargetConfig.Default(ModelKind.Level);
        config.UseSp = false;
        config.InitialPv = unit == "mm" ? 1000 : 50;
        config.InternalSp = unit == "mm" ? 1200 : 60;
        config.PvRange.Unit = config.SpRange.Unit = unit;
        config.PvRange.EngMax = config.SpRange.EngMax = unit == "mm" ? 2000 : 100;
        config.Params["height"] = height;
        config.Params["noise"] = config.Params["dead"] = config.Params["sens"] = 0;
        return ControlTarget.FromConfig(config);
    }

    [Theory]
    [InlineData("property")]
    [InlineData("wizard")]
    [InlineData("defaults")]
    public void HeightChangeInMillimetres_ReinitializesPhysicalStateAndClearsStaleHistory(string path)
    {
        var target = Level(height: path == "defaults" ? 3000 : 2000);
        using var plc = new DummyPlc();
        var log = new EventLog();
        target.SetPreview(true, log);
        target.Cycle(plc, 0.1, 0.1, log);
        target.SetPreview(false, log);
        target.SetForce(ForceKey.Pv, true, 1300, log);
        target.SetForce(ForceKey.Sp, true, 1500, log);
        Assert.True(target.ScheduleDisturbance(3, 1, null, log, out _));
        Assert.NotEqual(0, target.Elapsed);
        Assert.NotEqual(0, target.Trend.Count);

        if (path == "property") Assert.True(target.SetParam("height", 1000, log, out _));
        else if (path == "defaults") Assert.True(target.ResetParams(log));
        else
        {
            var scenario = ParameterCalculator.Scenarios(ModelKind.Level)[0];
            var inputs = scenario.Fields.ToDictionary(f => f.Key, f => f.Default);
            var calculated = ParameterCalculator.Calculate(ModelKind.Level, scenario, inputs);
            Assert.True(target.ApplyCalculatedParameters(calculated.Values, log, out _));
        }

        double newHeight = target.GetParam("height");
        Assert.Equal(1000 / newHeight * 100, target.Model.X, 10);
        Assert.Equal(1000, target.Pv);
        Assert.Equal(1200, target.Sp);
        Assert.Equal(1000, target.GetForce(ForceKey.Pv).Value);
        Assert.Equal(1200, target.GetForce(ForceKey.Sp).Value);
        Assert.Equal(0, target.Elapsed);
        Assert.Equal(0, target.Trend.Count);
        Assert.Equal(DisturbanceScheduleState.Cancelled, target.GetDisturbanceSchedule().State);
        Assert.Equal(1000, target.InitialPv);
        Assert.Equal(1200, target.InternalSp);
        target.Cycle(plc, 0.1, 0.2, log);
        Assert.Equal(1000, target.Pv);
        target.ReleaseForces(log);
        target.Cycle(plc, 0.1, 0.3, log);
        Assert.Equal(1000, target.Pv);
    }

    [Theory]
    [InlineData("property")]
    [InlineData("wizard")]
    [InlineData("defaults")]
    public void HeightChangeInPercent_DoesNotResetUnchangedUnitState(string path)
    {
        var target = Level("%", height: path == "defaults" ? 3000 : 2000);
        using var plc = new DummyPlc();
        var log = new EventLog();
        target.SetPreview(true, log);
        target.Cycle(plc, 0.1, 0.1, log);
        target.SetPreview(false, log);
        double state = target.Model.X;
        Assert.True(target.ScheduleDisturbance(3, 1, null, log, out _));
        if (path == "property") Assert.True(target.SetParam("height", 1000, log, out _));
        else if (path == "defaults") Assert.True(target.ResetParams(log));
        else Assert.True(target.ApplyCalculatedParameters(new Dictionary<string, double> { ["height"] = 1000 }, log, out _));
        Assert.Equal(state, target.Model.X);
        Assert.Equal(0.1, target.Elapsed);
        Assert.Equal(1, target.Trend.Count);
        Assert.Equal(DisturbanceScheduleState.Waiting, target.GetDisturbanceSchedule().State);
    }

    [Fact]
    public void ChangingOtherParametersOrKeepingSameHeight_DoesNotDiscardState()
    {
        var target = Level();
        using var plc = new DummyPlc();
        var log = new EventLog();
        target.SetPreview(true, log);
        target.Cycle(plc, 0.1, 0.1, log);
        target.SetPreview(false, log);
        Assert.True(target.SetParam("diameter", 800, log, out _));
        Assert.True(target.ApplyCalculatedParameters(new Dictionary<string, double> { ["height"] = 2000 }, log, out _));
        Assert.True(target.ResetParams(log));
        Assert.Equal(0.1, target.Elapsed);
        Assert.Equal(1, target.Trend.Count);
    }
}
