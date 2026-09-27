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
    public void ManualAlarmReset_ClearsAlarmOnlyAfterPlcAnswers()
    {
        var (eng, plc, t) = Setup(ModelKind.Level);
        t.Start(eng.Log, out _);
        plc.SetFault("D0", true);
        eng.StepOnce();

        Assert.True(t.ResetAlarm(eng.Log));
        eng.StepOnce();
        Assert.Equal(CommStatus.Timeout, t.Snapshot().Comm);

        plc.SetFault("D0", false);
        Assert.True(t.ResetAlarm(eng.Log));
        eng.StepOnce();
        Assert.Equal(CommStatus.Ok, t.Snapshot().Comm);
        Assert.Null(t.Snapshot().Alarm);
        Assert.Equal(RunState.Stopped, t.Snapshot().RunState);
    }

    [Fact]
    public void ResetAllAlarms_OnlyClearsRecoveredTargets_AndPreservesStoppedState()
    {
        using var plc = new DummyPlc();
        using var eng = new SimulationEngine(plc);
        var targets = Enumerable.Range(0, 3).Select(i => new ControlTarget($"Target {i}", ModelKind.Level, seed: 1)
        {
            MvAddress = $"D{i * 10}", PvAddress = $"D{i * 10 + 1}", UseSp = false,
        }).ToArray();
        foreach (var target in targets)
        {
            eng.Add(target);
            target.SetForce(ForceKey.Mv, true, 45, eng.Log);
            Assert.True(target.Start(eng.Log, out _));
        }
        for (int i = 0; i < 20; i++) eng.StepOnce();
        plc.SetFault(targets[0].MvAddress, true);
        plc.SetFault(targets[1].MvAddress, true);
        eng.StepOnce();
        targets[2].Stop(eng.Log);

        var before = targets.Select(t => t.Snapshot()).ToArray();
        var histories = targets.Select(t => Enumerable.Range(0, t.Trend.Count).Select(i => t.Trend[i]).ToArray()).ToArray();
        int normalLogCount = eng.Log.Snapshot().Count(e => e.TargetId == targets[2].Id);
        plc.SetFault(targets[0].MvAddress, false);
        Assert.Equal(2, eng.ResetAllAlarms());
        eng.StepOnce();

        Assert.Equal(CommStatus.Ok, targets[0].Comm);
        Assert.Null(targets[0].Alarm);
        Assert.Equal(CommStatus.Timeout, targets[1].Comm);
        Assert.NotNull(targets[1].Alarm);
        Assert.Equal(CommStatus.Ok, targets[2].Comm);
        Assert.Null(targets[2].Alarm);
        Assert.False(targets[2].ResetAlarm(eng.Log));
        Assert.Equal(normalLogCount, eng.Log.Snapshot().Count(e => e.TargetId == targets[2].Id));
        for (int i = 0; i < targets.Length; i++)
        {
            var after = targets[i].Snapshot();
            Assert.Equal(RunState.Stopped, after.RunState);
            Assert.False(after.PvWriting);
            Assert.True(before[i].Elapsed > 0);
            Assert.Equal(before[i].Pv, after.Pv);
            Assert.Equal(before[i].Elapsed, after.Elapsed);
            Assert.Equal(before[i].ActiveForces, after.ActiveForces);
            Assert.Equal((true, 45d), targets[i].GetForce(ForceKey.Mv));
            Assert.Equal(histories[i].Length + 1, targets[i].Trend.Count);
            Assert.Equal(histories[i], Enumerable.Range(0, histories[i].Length).Select(j => targets[i].Trend[j]));
        }
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
