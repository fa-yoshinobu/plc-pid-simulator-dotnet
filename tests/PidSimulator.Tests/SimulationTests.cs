using PidSimulator.Core;
using PidSimulator.Core.Plc;

namespace PidSimulator.Tests;

public class SimulationTests
{
    private static (SimulationEngine eng, DummyPlc plc, ControlTarget t) Setup(ModelKind kind)
    {
        var plc = new DummyPlc();
        var eng = new SimulationEngine(plc);
        var t = new ControlTarget("TEST", kind, seed: 1) { MvAddress = "D0", PvAddress = "D1", SpAddress = "D2" };
        eng.Add(t);
        plc.AddLoop(DummyPidLoop.For(t));
        return (eng, plc, t);
    }

    [Theory]
    [InlineData(ModelKind.Motor)]
    [InlineData(ModelKind.Flow)]
    [InlineData(ModelKind.Level)]
    [InlineData(ModelKind.Heater)]
    [InlineData(ModelKind.Steam)]
    [InlineData(ModelKind.Chiller)]
    [InlineData(ModelKind.Pressure)]
    [InlineData(ModelKind.PumpFlow)]
    [InlineData(ModelKind.LevelPumpIn)]
    [InlineData(ModelKind.LevelPumpOut)]
    [InlineData(ModelKind.LevelDrainValve)]
    [InlineData(ModelKind.CoolingWater)]
    [InlineData(ModelKind.PressureSupplyValve)]
    [InlineData(ModelKind.PressureExhaustValve)]
    [InlineData(ModelKind.PumpPressure)]
    public void ClosedLoop_WithDummyPlcPid_SettlesAtSetpoint(ModelKind kind)
    {
        var (eng, _, t) = Setup(kind);
        Assert.True(t.Start(eng.Log, out _));
        for (int i = 0; i < 6000; i++) eng.StepOnce(); // 600 s

        var tail = new List<TrendSample>();
        lock (t.Sync) t.Trend.CopyRange(eng.T - 20, eng.T, tail);
        double mean = tail.Average(s => s.Pv);
        double tol = t.Info.Span * 0.03;
        Assert.InRange(mean, t.Info.DefaultSp - tol, t.Info.DefaultSp + tol);
    }

    [Fact]
    public void CommTimeout_WithStopAction_StopsAndRaisesAlarm()
    {
        var (eng, plc, t) = Setup(ModelKind.Level);
        t.Start(eng.Log, out _);
        for (int i = 0; i < 10; i++) eng.StepOnce();
        plc.SetFault("D0", true);
        eng.StepOnce();

        var s = t.Snapshot();
        Assert.Equal(RunState.Stopped, s.RunState);
        Assert.Equal(CommStatus.Timeout, s.Comm);
        Assert.NotNull(s.Alarm);
        Assert.False(t.Start(eng.Log, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void ManualResume_ClearsAlarmOnlyAfterPlcAnswers()
    {
        var (eng, plc, t) = Setup(ModelKind.Level);
        t.Start(eng.Log, out _);
        plc.SetFault("D0", true);
        eng.StepOnce();

        t.RequestResume();
        eng.StepOnce();
        Assert.Equal(CommStatus.Timeout, t.Snapshot().Comm);

        plc.SetFault("D0", false);
        t.RequestResume();
        eng.StepOnce();
        Assert.Equal(CommStatus.Ok, t.Snapshot().Comm);
        Assert.Null(t.Snapshot().Alarm);
    }

    [Fact]
    public void NaNPvForce_IsNeverWrittenToPlc()
    {
        var (eng, plc, t) = Setup(ModelKind.Heater);
        t.Start(eng.Log, out _);
        for (int i = 0; i < 5; i++) eng.StepOnce();
        plc.TryRead("D1", out double before);

        t.SetForce(ForceKey.Pv, true, double.NaN, eng.Log);
        eng.StepOnce();

        plc.TryRead("D1", out double after);
        Assert.Equal(before, after);
        Assert.Contains("PV値異常", t.Snapshot().Alarm);
    }

    [Fact]
    public void Stop_WithInitialMode_WritesInitialPvOnce()
    {
        var (eng, plc, t) = Setup(ModelKind.Level);
        t.StopPv = StopPvMode.Initial;
        t.Start(eng.Log, out _);
        for (int i = 0; i < 300; i++) eng.StepOnce();
        t.Stop(eng.Log);
        eng.StepOnce();

        plc.TryRead("D1", out double raw);
        Assert.Equal(PlcDataTypes.Clamp(t.DataType, t.PvRange.ToRaw(t.InitialPv)), raw);
        Assert.False(t.Snapshot().PvWriting);
    }

    [Fact]
    public void MvForce_OverridesPlcMv_AndReleaseAllClearsIt()
    {
        var (eng, _, t) = Setup(ModelKind.Motor);
        t.Start(eng.Log, out _);
        t.SetForce(ForceKey.Mv, true, 45, eng.Log);
        eng.StepOnce();
        Assert.Equal(45, t.Snapshot().Mv, 6);

        Assert.Equal(1, eng.ReleaseAllForces());
        Assert.Empty(t.Snapshot().ActiveForces);
    }
}
