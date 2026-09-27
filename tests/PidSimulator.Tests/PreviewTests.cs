using PidSimulator.Core;
using PidSimulator.Core.Plc;

namespace PidSimulator.Tests;

public class PreviewTests
{
    [Fact]
    public void RelayPreview_FollowsOnOffPattern_WithoutPlcReadsOrWrites()
    {
        var target = CreateFlowTarget(onOff: true);
        var log = new EventLog();
        using var plc = new RecordingPlc();
        target.SetPreview(true, log);

        double time = 0;
        foreach (var (requested, expectedMv) in new[] { (0d, 0d), (100d, 100d), (49d, 0d), (50d, 100d), (-1d, 0d), (101d, 100d) })
        {
            target.SetPreviewMv(requested);
            target.Cycle(plc, 0.1, time += 0.1, log);
            var state = target.Snapshot();
            Assert.Equal(RunState.Preview, state.RunState);
            Assert.Equal(expectedMv, state.PreviewMv);
            Assert.Equal(expectedMv, state.Mv);
            Assert.Equal(expectedMv * 0.6, state.Pv, 8);
            Assert.False(state.PvWriting);
        }

        Assert.Equal(0, plc.Reads);
        Assert.Equal(0, plc.Writes);
    }

    [Fact]
    public void ContinuousPreview_KeepsIntermediateMv_AndProportionalModelResponse()
    {
        var target = CreateFlowTarget(onOff: false);
        var log = new EventLog();
        using var plc = new RecordingPlc();
        target.SetPreview(true, log);
        target.SetPreviewMv(37.5);
        target.Cycle(plc, 0.1, 0.1, log);

        Assert.Equal(37.5, target.PreviewMv);
        Assert.Equal(37.5, target.Mv);
        Assert.Equal(22.5, target.Pv, 8);
        Assert.False(target.PvWriting);
        Assert.Equal(0, plc.Writes);
    }

    [Theory]
    [InlineData(30, 0)]
    [InlineData(50, 100)]
    [InlineData(75, 100)]
    public void StartingPreview_AfterChangingToRelay_ConvertsPreviousContinuousMv(double previousMv, double expectedMv)
    {
        var target = CreateFlowTarget(onOff: false);
        var log = new EventLog();
        using var plc = new RecordingPlc();
        target.SetPreview(true, log);
        target.SetPreviewMv(previousMv);
        target.Cycle(plc, 0.1, 0.1, log);
        target.SetPreview(false, log);

        var settings = target.ToConfig();
        settings.MvOnOff = true;
        target.ApplySettings(settings);
        target.SetPreview(true, log);

        Assert.Equal(expectedMv, target.PreviewMv);
        target.Cycle(plc, 0.1, 0.2, log);
        Assert.Equal(expectedMv, target.Mv);
        Assert.Equal(expectedMv * 0.6, target.Pv, 8);
        Assert.Equal(0, plc.Writes);
    }

    [Theory]
    [InlineData(false, 60)]
    [InlineData(true, 100)]
    public void NonFinitePreviewMv_IsIgnored(bool onOff, double expectedMv)
    {
        var target = CreateFlowTarget(onOff);
        target.SetPreviewMv(60);
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            target.SetPreviewMv(invalid);
            Assert.Equal(expectedMv, target.PreviewMv);
        }
    }

    [Fact]
    public void RelayPreview_DiagnosticMvForceStillOverridesRelayInput()
    {
        var target = CreateFlowTarget(onOff: true);
        var log = new EventLog();
        using var plc = new RecordingPlc();
        target.SetPreview(true, log);
        target.SetPreviewMv(100);
        target.SetForce(ForceKey.Mv, true, 40, log);
        target.Cycle(plc, 0.1, 0.1, log);
        Assert.Equal(100, target.PreviewMv);
        Assert.Equal(40, target.Mv);
        Assert.Equal(24, target.Pv, 8);
        Assert.Equal(0, plc.Writes);
    }

    [Theory]
    [InlineData(StopPvMode.Initial)]
    [InlineData(StopPvMode.Value)]
    public void StartingPreview_CancelsQueuedStopPvWrite(StopPvMode stopMode)
    {
        var target = CreateFlowTarget(onOff: true);
        target.StopPv = stopMode;
        target.StopPvValue = 17;
        var log = new EventLog();
        using var plc = new RecordingPlc();
        Assert.True(target.Start(log, out _));
        target.Stop(log); // Queues the configured stop value for the next PLC cycle.
        target.SetPreview(true, log);
        target.SetPreviewMv(100);
        target.Cycle(plc, 0.1, 0.1, log);
        Assert.False(target.PvWriting);
        Assert.Equal(0, plc.Writes);

        // Finishing preview must not flush a stale stop value to the PLC either.
        target.SetPreview(false, log);
        target.Cycle(plc, 0.1, 0.2, log);
        Assert.Equal(0, plc.Reads);
        Assert.Equal(0, plc.Writes);
    }

    [Theory]
    [InlineData(RecoverMode.Auto)]
    [InlineData(RecoverMode.Manual)]
    public void Preview_AfterCommunicationFailure_DoesNotPollForRecovery(RecoverMode recovery)
    {
        var target = CreateFlowTarget(onOff: true);
        target.Recover = recovery;
        var log = new EventLog();
        using var failedPlc = new RecordingPlc();
        Assert.True(target.Start(log, out _));
        target.Cycle(failedPlc, 0.1, 0.1, log);
        Assert.Equal(RunState.Stopped, target.RunState);
        Assert.Equal(CommStatus.Timeout, target.Comm);
        if (recovery == RecoverMode.Manual) Assert.True(target.ResetAlarm(log));

        using var previewPlc = new RecordingPlc();
        target.SetPreview(true, log);
        target.SetPreviewMv(100);
        target.Cycle(previewPlc, 0.1, 2, log);
        Assert.Equal(60, target.Pv, 8);
        target.SetPreviewMv(0);
        target.Cycle(previewPlc, 0.1, 4, log);
        Assert.Equal(0, target.Pv, 8);
        Assert.Equal(RunState.Preview, target.RunState);
        Assert.Equal(CommStatus.Timeout, target.Comm);
        Assert.False(target.PvWriting);
        Assert.Equal(0, previewPlc.Reads);
        Assert.Equal(0, previewPlc.Writes);

        // A requested reset waits for preview to finish and for an actual response.
        target.SetPreview(false, log);
        previewPlc.ReadResult = PlcIoStatus.Pending;
        target.Cycle(previewPlc, 0.1, 5, log);
        Assert.Equal(CommStatus.Timeout, target.Comm);
        Assert.NotNull(target.Alarm);
        previewPlc.ReadResult = PlcIoStatus.Ok;
        target.Cycle(previewPlc, 0.1, 7, log);
        Assert.Equal(CommStatus.Ok, target.Comm);
        Assert.Null(target.Alarm);
        Assert.Equal(RunState.Stopped, target.RunState);
        Assert.Equal(2, previewPlc.Reads);
        Assert.Equal(0, previewPlc.Writes);
    }

    [Fact]
    public void Preview_AlarmResetPreservesStateWithoutPlcIo_AndPersistentCauseRaisesItAgain()
    {
        var target = CreateFlowTarget(onOff: false);
        var log = new EventLog();
        using var plc = new RecordingPlc();
        target.SetPreview(true, log);
        target.SetPreviewMv(45);
        target.Cycle(plc, 0.1, 0.1, log);
        target.SetForce(ForceKey.Mv, true, double.NaN, log);
        target.Cycle(plc, 0.1, 0.2, log);
        Assert.Contains("MV値異常", target.Alarm);
        var before = target.Snapshot();
        var history = Enumerable.Range(0, target.Trend.Count).Select(i => target.Trend[i]).ToArray();

        Assert.True(target.ResetAlarm(log));
        Assert.Null(target.Alarm);
        Assert.Equal(RunState.Preview, target.RunState);
        Assert.Equal(before.Pv, target.Pv);
        Assert.Equal(before.Elapsed, target.Elapsed);
        Assert.Equal(before.ActiveForces, target.Snapshot().ActiveForces);
        Assert.Equal((true, double.NaN), target.GetForce(ForceKey.Mv));
        Assert.Equal(history, Enumerable.Range(0, target.Trend.Count).Select(i => target.Trend[i]));

        target.Cycle(plc, 0.1, 0.3, log);
        Assert.Contains("MV値異常", target.Alarm);
        target.SetForce(ForceKey.Mv, false, 0, log);
        Assert.True(target.ResetAlarm(log));
        target.Cycle(plc, 0.1, 0.4, log);
        Assert.Null(target.Alarm);
        Assert.Equal(RunState.Preview, target.RunState);
        Assert.Equal(0, plc.Reads);
        Assert.Equal(0, plc.Writes);
    }

    private static ControlTarget CreateFlowTarget(bool onOff)
    {
        var target = new ControlTarget("Preview", ModelKind.Flow) { MvOnOff = onOff, UseSp = false };
        // Remove transient/filter/noise effects so the physical flow directly exposes the applied MV.
        target.Model.P["qmax"] = 60;
        foreach (string key in new[] { "tau", "dead", "sens", "noise", "res", "pvar" }) target.Model.P[key] = 0;
        target.Model.Reset(0);
        return target;
    }

    private sealed class RecordingPlc : IPlcClient
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public PlcIoStatus ReadResult { get; set; } = PlcIoStatus.Failed;
        public string Endpoint => "preview-test";
        public PlcStatus Status => new(PlcConnectionState.Disconnected, Endpoint, null, 0, 0);
        public PlcIoStatus Read(string address, string dataType, out double raw)
        {
            Reads++;
            raw = 0;
            return ReadResult;
        }
        public PlcIoStatus Write(string address, string dataType, double raw)
        {
            Writes++;
            return PlcIoStatus.Failed;
        }
        public Task<PlcTestResult> TestReadAsync(string address, string dataType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PlcTestResult> TestWriteAsync(string address, string dataType, double raw, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
