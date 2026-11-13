using PidSimulator.Core;
using PidSimulator.Core.Plc;

namespace PidSimulator.Tests;

public class TargetResponsibilitiesTests
{
    [Fact]
    public void ForceChangesLogOnlyTransitionsAndReleaseKeepsTheStoredValues()
    {
        var target = new ControlTarget("test", ModelKind.Flow);
        var log = new EventLog();
        target.SetForce(ForceKey.Mv, false, 45, log);
        Assert.Empty(log.Snapshot());
        target.SetForce(ForceKey.Mv, true, 45, log);
        target.SetForce(ForceKey.Mv, true, 45, log);
        target.SetForce(ForceKey.Mv, true, 50, log);
        target.SetForce(ForceKey.Mv, false, 100, log);
        target.SetForce(ForceKey.Mv, false, 25, log);
        Assert.Equal(3, log.Snapshot().Count);
        Assert.Equal((false, 25d), target.GetForce(ForceKey.Mv));

        target.SetForce(ForceKey.Mv, true, 25, log);
        target.SetForce(ForceKey.Pv, true, 12.5, log);
        Assert.Equal(2, target.ReleaseForces(log, "test release"));
        Assert.Equal(0, target.ReleaseForces(log, "test release"));
        Assert.Empty(target.Snapshot().ActiveForces);
        Assert.Equal((false, 25d), target.GetForce(ForceKey.Mv));
        Assert.Equal((false, 12.5), target.GetForce(ForceKey.Pv));
        Assert.Equal(7, log.Snapshot().Count);
        Assert.All(log.Snapshot().TakeLast(2), entry => Assert.Contains("test release", entry.Message));
    }

    [Fact]
    public void PendingManualRecoveryKeepsTheRequestAndCycleReadsBeforeWritingForcedPv()
    {
        using var plc = new ScriptedPlc();
        using var engine = new SimulationEngine(plc);
        var target = new ControlTarget("test", ModelKind.Flow)
        { MvAddress = "D0", PvAddress = "D2", UseSp = false };
        engine.Add(target);
        target.Start(engine.Log, out _);
        target.SetForce(ForceKey.Mv, true, 60, engine.Log);
        target.SetForce(ForceKey.Pv, true, 12.5, engine.Log);

        engine.StepOnce();
        Assert.Equal(RunState.Stopped, target.RunState);
        Assert.Equal(CommStatus.Timeout, target.Comm);
        Assert.True(target.ResetAlarm(engine.Log));
        engine.StepOnce();
        Assert.Equal(CommStatus.Timeout, target.Comm);
        engine.StepOnce();
        Assert.Equal(CommStatus.Ok, target.Comm);
        Assert.Null(target.Alarm);
        Assert.Equal(RunState.Stopped, target.RunState);
        Assert.True(target.Start(engine.Log, out _));
        engine.StepOnce();

        Assert.Equal(new[] { "read:D0", "read:D0", "read:D0", "read:D0", "write:D2" }, plc.Calls);
        Assert.Equal(target.PvRange.ToRaw(12.5), plc.WrittenRaw);
        Assert.Equal(60, target.Mv);
        Assert.Equal(12.5, target.Pv);
        Assert.Single(engine.Log.Snapshot(), entry => entry.Message.StartsWith("通信タイムアウト"));
        Assert.Single(engine.Log.Snapshot(), entry => entry.Message == "通信復旧（手動再開）");
    }

    private sealed class ScriptedPlc : IPlcClient
    {
        private readonly Queue<PlcIoStatus> _reads = new([
            PlcIoStatus.Failed, PlcIoStatus.Pending, PlcIoStatus.Ok, PlcIoStatus.Ok]);
        public List<string> Calls { get; } = [];
        public double WrittenRaw { get; private set; }
        public string Endpoint => "test";
        public PlcStatus Status => new(PlcConnectionState.Connected, Endpoint, null, 0, 0);
        public PlcIoStatus Read(string address, string dataType, out double raw)
        {
            Calls.Add("read:" + address);
            raw = 2000;
            return _reads.Dequeue();
        }
        public PlcIoStatus Write(string address, string dataType, double raw)
        {
            Calls.Add("write:" + address);
            WrittenRaw = raw;
            return PlcIoStatus.Ok;
        }
        public void Tick(double dt) { }
        public Task<PlcTestResult> TestReadAsync(string address, string dataType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PlcTestResult> TestWriteAsync(string address, string dataType, double raw, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
