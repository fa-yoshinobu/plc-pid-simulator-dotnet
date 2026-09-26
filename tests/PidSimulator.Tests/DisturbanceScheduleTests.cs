using PidSimulator.Core;
using PidSimulator.Core.Plc;

namespace PidSimulator.Tests;

public class DisturbanceScheduleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelayedPulse_AppliesOnExactSimulationBoundaries(bool preview)
    {
        using var plc = new DummyPlc();
        using var engine = new SimulationEngine(plc);
        var target = new ControlTarget("外乱試験", ModelKind.Flow) { UseSp = false };
        engine.Add(target);
        Assert.True(target.ScheduleDisturbance(7, 0.3, 0.2, engine.Log, out _));
        if (preview) target.SetPreview(true, engine.Log);
        else Assert.True(target.Start(engine.Log, out _));

        var applied = new List<double>();
        for (int i = 0; i < 8; i++)
        {
            engine.StepOnce();
            applied.Add(target.Trend[target.Trend.Count - 1].Dist);
        }
        Assert.Equal(new double[] { 0, 0, 0, 7, 7, 0, 0, 0 }, applied);
        Assert.Equal(DisturbanceScheduleState.Completed, target.GetDisturbanceSchedule().State);
        Assert.False(target.DisturbanceOn);
        Assert.Single(engine.Log.Snapshot(), e => e.Message.StartsWith("予約外乱を投入"));
        Assert.Single(engine.Log.Snapshot(), e => e.Message == "予約外乱が終了");
    }

    [Fact]
    public void WaitingWhileStopped_DoesNotConsumeDelay_AndIgnoresWallTime()
    {
        using var plc = new DummyPlc();
        var target = new ControlTarget("外乱試験", ModelKind.Flow);
        var log = new EventLog();
        Assert.True(target.ScheduleDisturbance(5, 0.1, null, log, out _));
        for (int i = 0; i < 10; i++) target.Cycle(plc, 0.1, i * 100, log);
        Assert.Equal(0.1, target.GetDisturbanceSchedule().SecondsUntilStart, 9);
        target.SetPreview(true, log);
        target.Cycle(plc, 0.1, 2000, log);
        Assert.Equal(0, target.Snapshot().ActualDisturbance);
        target.Cycle(plc, 0.1, 9000, log);
        Assert.Equal(5, target.Snapshot().ActualDisturbance);
    }

    [Fact]
    public void ImmediateStep_ContinuesUntilManualRelease()
    {
        using var plc = new DummyPlc();
        using var engine = new SimulationEngine(plc);
        var target = new ControlTarget("外乱試験", ModelKind.Flow);
        engine.Add(target);
        target.SetPreview(true, engine.Log);
        Assert.True(target.ScheduleDisturbance(6, 0, null, engine.Log, out _));
        for (int i = 0; i < 100; i++)
        {
            engine.StepOnce();
            Assert.Equal(6, target.Snapshot().ActualDisturbance);
        }
        target.SetDisturbance(false, engine.T, engine.Log);
        engine.StepOnce();
        Assert.Equal(0, target.Snapshot().ActualDisturbance);
        Assert.Equal(DisturbanceScheduleState.Cancelled, target.GetDisturbanceSchedule().State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void StopOrReset_CancelsPendingAndActiveSchedules(bool reset, bool active)
    {
        using var plc = new DummyPlc();
        using var engine = new SimulationEngine(plc);
        var target = new ControlTarget("外乱試験", ModelKind.Flow);
        engine.Add(target);
        target.SetPreview(true, engine.Log);
        Assert.True(target.ScheduleDisturbance(6, active ? 0 : 10, null, engine.Log, out _));
        engine.StepOnce();
        if (reset) target.Reset(engine.Log);
        else target.SetPreview(false, engine.Log);
        Assert.Equal(DisturbanceScheduleState.Cancelled, target.GetDisturbanceSchedule().State);
        Assert.Equal(0, target.Snapshot().ActualDisturbance);
        target.SetPreview(true, engine.Log);
        for (int i = 0; i < 120; i++) engine.StepOnce();
        Assert.Equal(0, target.Snapshot().ActualDisturbance);
    }

    [Fact]
    public void ForceOverridesPulse_WhileScheduleStillFinishes()
    {
        using var plc = new DummyPlc();
        using var engine = new SimulationEngine(plc);
        var target = new ControlTarget("外乱試験", ModelKind.Flow);
        engine.Add(target);
        target.SetPreview(true, engine.Log);
        Assert.True(target.ScheduleDisturbance(6, 0, 0.2, engine.Log, out _));
        target.SetForce(ForceKey.Disturbance, true, 9, engine.Log);
        engine.StepOnce();
        Assert.Equal(9, target.Trend[0].Dist);
        target.SetForce(ForceKey.Disturbance, false, 9, engine.Log);
        engine.StepOnce();
        Assert.Equal(6, target.Trend[1].Dist);
        target.SetForce(ForceKey.Disturbance, true, 9, engine.Log);
        engine.StepOnce();
        Assert.Equal(9, target.Trend[2].Dist);
        Assert.Equal(DisturbanceScheduleState.Completed, target.GetDisturbanceSchedule().State);
        target.ReleaseForces(engine.Log);
        engine.StepOnce();
        Assert.Equal(0, target.Trend[3].Dist);
    }

    [Fact]
    public void ManualInput_ReplacesSchedule_AndRemainsCompatibleWithStop()
    {
        using var plc = new DummyPlc();
        using var engine = new SimulationEngine(plc);
        var target = new ControlTarget("外乱試験", ModelKind.Flow);
        engine.Add(target);
        target.SetPreview(true, engine.Log);
        Assert.True(target.ScheduleDisturbance(6, 1, null, engine.Log, out _));
        target.SetDisturbanceValue(3);
        target.SetDisturbance(true, engine.T, engine.Log);
        engine.StepOnce();
        Assert.Equal(3, target.Snapshot().ActualDisturbance);
        Assert.Equal(DisturbanceScheduleState.Cancelled, target.GetDisturbanceSchedule().State);
        target.Stop(engine.Log);
        Assert.Equal(3, target.Snapshot().ActualDisturbance);
    }

    [Fact]
    public void InvalidSchedule_DoesNotReplaceValidSchedule()
    {
        var target = new ControlTarget("外乱試験", ModelKind.Flow);
        var log = new EventLog();
        Assert.True(target.ScheduleDisturbance(3, 1, 2, log, out _));
        foreach (var (value, delay, duration) in new[]
        {
            (double.NaN, 1d, 2d), (double.PositiveInfinity, 1d, 2d), (-1d, 1d, 2d), (target.Info.DistMax + 1, 1d, 2d),
            (3d, -1d, 2d), (3d, double.NaN, 2d), (3d, double.PositiveInfinity, 2d), (3d, 86401d, 2d),
            (3d, 1d, 0d), (3d, 1d, 0.05d), (3d, 1d, double.NaN), (3d, 1d, double.PositiveInfinity), (3d, 1d, 86401d),
        })
        {
            Assert.False(target.ScheduleDisturbance(value, delay, duration, log, out var error));
            Assert.NotNull(error);
            var schedule = target.GetDisturbanceSchedule();
            Assert.Equal(3, schedule.Value);
            Assert.Equal(1, schedule.SecondsUntilStart);
            Assert.Equal(2, schedule.SecondsRemaining);
        }
    }

    [Fact]
    public void StopAll_CancelsArmedSchedulesEvenOnStoppedTargets()
    {
        using var plc = new DummyPlc();
        using var engine = new SimulationEngine(plc);
        var target = new ControlTarget("外乱試験", ModelKind.Flow);
        engine.Add(target);
        Assert.True(target.ScheduleDisturbance(3, 1, null, engine.Log, out _));
        engine.StopAll("試験停止");
        Assert.Equal(DisturbanceScheduleState.Cancelled, target.GetDisturbanceSchedule().State);
    }

    [Fact]
    public void RegistrationHeightChange_CancelsArmedScheduleBeforeResettingElapsed()
    {
        using var plc = new DummyPlc();
        var target = new ControlTarget("液面試験", ModelKind.Level);
        var log = new EventLog();
        target.SetPreview(true, log);
        target.Cycle(plc, 0.1, 0.1, log);
        target.SetPreview(false, log);
        Assert.True(target.ScheduleDisturbance(3, 1, null, log, out _));
        var config = target.ToConfig();
        config.Params["height"] *= 2;
        target.ApplySettings(config);
        Assert.Equal(0, target.Elapsed);
        Assert.Equal(DisturbanceScheduleState.Cancelled, target.GetDisturbanceSchedule().State);
        Assert.False(target.DisturbanceOn);

        // A later registration edit must not silently turn off a manual disturbance.
        target.SetDisturbanceValue(4);
        target.SetDisturbance(true, 0.1, log);
        config.Params["height"] *= 2;
        target.ApplySettings(config);
        Assert.True(target.DisturbanceOn);
        Assert.Equal(4, target.Snapshot().ActualDisturbance);
    }
}
