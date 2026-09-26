using PidSimulator.Core;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public class EngineeringUnitsTests
{
    [Theory]
    [InlineData(ModelKind.Level)]
    [InlineData(ModelKind.LevelPumpIn)]
    [InlineData(ModelKind.LevelPumpOut)]
    [InlineData(ModelKind.LevelDrainValve)]
    public void MillimetreLevel_HasSameWaterBalanceAsPercent_AndWritesFractionalPv(ModelKind kind)
    {
        var percent = Level(kind, "%");
        var millimetres = Level(kind, "mm");
        using var percentPlc = new DummyPlc();
        using var mmPlc = new DummyPlc();
        var log = new EventLog();
        percentPlc.Write("D0", "FLOAT32", 42.5);
        mmPlc.Write("D0", "FLOAT32", 42.5);
        Assert.True(percent.Start(log, out _));
        Assert.True(millimetres.Start(log, out _));
        for (int i = 1; i <= 100; i++)
        {
            percent.Cycle(percentPlc, .1, i * .1, log);
            millimetres.Cycle(mmPlc, .1, i * .1, log);
        }
        Assert.Equal(percent.Pv * 20, millimetres.Pv, 8);
        Assert.Equal(percent.Model.X, millimetres.Model.X, 8);
        Assert.Equal(PlcIoStatus.Ok, mmPlc.Read("D2", "FLOAT32", out double written));
        Assert.Equal((double)(float)millimetres.Pv, written);
        Assert.NotEqual(Math.Truncate(written), written);
        millimetres.Reset(log);
        Assert.Equal(1000, millimetres.Pv);
        Assert.Equal(50, millimetres.Model.X);
    }

    [Theory]
    [InlineData(ModelKind.PumpFlow, "Hz", 0, 60, 30)]
    [InlineData(ModelKind.PumpFlow, "rpm", 0, 1800, 900)]
    [InlineData(ModelKind.Flow, "V", 0, 10, 5)]
    [InlineData(ModelKind.Flow, "mA", 4, 20, 12)]
    public void EngineeringMv_NormalizesModelInput_AndKeepsDisplayAndTrendUnits(ModelKind kind, string unit, double min, double max, double mv)
    {
        var config = TargetConfig.Default(kind);
        config.UseSp = false;
        config.MvRange = new RangeDef { Unit = unit, EngMin = min, EngMax = max };
        foreach (string key in new[] { "tau", "dead", "sens", "noise", "res", "pvar" }) config.Params[key] = 0;
        config.Params["qmax"] = 60;
        var target = ControlTarget.FromConfig(config);
        var log = new EventLog();
        using var plc = new DummyPlc();
        target.SetPreview(true, log);
        target.SetPreviewMv(mv);
        target.Cycle(plc, .1, .1, log);
        Assert.Equal(mv, target.Mv);
        Assert.Equal(mv, target.Trend[0].Mv);
        Assert.Equal(30, target.Pv, 8);
        target.SetForce(ForceKey.Mv, true, max, log);
        target.Cycle(plc, .1, .2, log);
        Assert.Equal(60, target.Pv, 8);
        Assert.Equal(max, target.Mv);
    }

    [Fact]
    public void HeaterKilowatts_ApplyHeatBalance_AndKeepDisplayAndTrendUnits()
    {
        var config = TargetConfig.Default(ModelKind.Heater);
        config.UseSp = false;
        config.MvRange = new RangeDef { Unit = "kW", EngMin = 0, EngMax = 8 };
        config.InitialPv = 25;
        config.Params["pmax"] = 8;
        config.Params["cap"] = 2;
        foreach (string key in new[] { "dead", "sens", "noise", "hloss" }) config.Params[key] = 0;
        var target = ControlTarget.FromConfig(config);
        var log = new EventLog();
        using var plc = new DummyPlc();

        target.SetPreview(true, log);
        target.SetPreviewMv(4);
        target.Cycle(plc, .1, .1, log);
        Assert.Equal(4, target.Mv);
        Assert.Equal(4, target.Trend[0].Mv);
        Assert.Equal(25.2, target.Pv, 8); // 4 kW * 0.1 s / 2 kJ/degree

        target.SetForce(ForceKey.Mv, true, 8, log);
        target.Cycle(plc, .1, .2, log);
        Assert.Equal(25.6, target.Pv, 8);
        Assert.Equal(8, target.Mv);
        Assert.Equal(8, target.Trend[1].Mv);
    }

    [Fact]
    public void PercentMv_SubrangeRetainsItsPhysicalPercentage()
    {
        var range = new RangeDef { Unit = "%", EngMin = 20, EngMax = 80 };
        Assert.Equal(20, EngineeringUnits.MvToPercent(range, 20));
        Assert.Equal(80, EngineeringUnits.MvToPercent(range, 80));
    }

    [Fact]
    public void ChangingLevelUnit_ResetsInMatchingUnitsAndClearsOldTrend()
    {
        var target = Level(ModelKind.Level, "%");
        var config = Level(ModelKind.Level, "mm").ToConfig();
        target.Trend.Add(new TrendSample(1, 60, 50, 0, 0, false));
        target.ApplySettings(config);
        Assert.Equal(1000, target.Pv);
        Assert.Equal(50, target.Model.X);
        Assert.Equal(1200, target.Sp);
        Assert.Equal(0, target.Trend.Count);
        using var plc = new DummyPlc();
        target.Cycle(plc, .1, 2, new EventLog());
        Assert.Equal(1000, target.Pv);
    }

    [Theory]
    [InlineData(StopPvMode.Initial, 1000)]
    [InlineData(StopPvMode.Value, 750)]
    public void StopPvInMillimetres_ResetsModelAndWritesTheSameHeight(StopPvMode mode, double expected)
    {
        var target = Level(ModelKind.Level, "mm");
        target.StopPv = mode;
        target.StopPvValue = 750;
        using var plc = new DummyPlc();
        var log = new EventLog();
        target.Start(log, out _);
        target.Stop(log);
        target.Cycle(plc, .1, .1, log);
        Assert.Equal(expected, target.Pv);
        Assert.Equal(expected / 20, target.Model.X);
        plc.Read("D2", "FLOAT32", out double written);
        Assert.Equal(expected, written);
    }

    [Fact]
    public void ProjectRoundtrip_PreservesRealRangesAndPhysicalUnits()
    {
        var config = Level(ModelKind.LevelPumpIn, "mm").ToConfig();
        config.MvRange = new RangeDef { RawMin = 0, RawMax = 60, EngMin = 0, EngMax = 60, Unit = "Hz" };
        config.PvRange.RawMax = 2000.5;
        config.Params["diameter"] = 500;
        var document = new ProjectDocument { Targets = [config] };
        var restored = ProjectSerializer.FromJson(ProjectSerializer.ToJson(document));
        var target = ControlTarget.FromConfig(restored.Targets[0]);
        Assert.Equal(2000.5, target.PvRange.RawMax);
        Assert.Equal("Hz", target.MvRange.Unit);
        Assert.Equal("mm", target.PvRange.Unit);
        Assert.Equal(500, target.Model.P["diameter"]);
    }

    private static ControlTarget Level(ModelKind kind, string unit)
    {
        var config = TargetConfig.Default(kind);
        config.MvAddress = "D0";
        config.PvAddress = "D2";
        config.UseSp = false;
        config.DataType = "FLOAT32";
        config.Params["height"] = 2000;
        config.Params["diameter"] = 0;
        config.Params["area"] = 1;
        config.Params["noise"] = config.Params["sens"] = config.Params["dead"] = 0;
        config.MvRange.RawMax = 100;
        double full = unit == "mm" ? 2000 : 100;
        config.PvRange = new RangeDef { RawMax = full, EngMax = full, Unit = unit };
        config.SpRange = config.PvRange.Clone();
        config.InitialPv = full / 2;
        config.InternalSp = full * .6;
        return ControlTarget.FromConfig(config);
    }
}
