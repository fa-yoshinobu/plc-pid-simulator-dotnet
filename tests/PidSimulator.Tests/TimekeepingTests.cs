using PidSimulator.Core;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public class TimekeepingTests
{
    [Fact]
    public void ResumeKeepsOldLabelsAndModelStepsWhileRecordingOneInterruption()
    {
        var clock = new ManualClock();
        using var plc = new DummyPlc();
        using var referencePlc = new DummyPlc();
        using var engine = new SimulationEngine(plc, clock);
        using var reference = new SimulationEngine(referencePlc, clock);
        var target = Target();
        var referenceTarget = Target();
        engine.Add(target);
        reference.Add(referenceTarget);
        target.Start(engine.Log, out _);
        referenceTarget.Start(reference.Log, out _);
        plc.TryWrite("D0", 2000);
        referencePlc.TryWrite("D0", 2000);
        for (int i = 0; i < 50; i++)
        {
            clock.Advance(0.1);
            engine.StepRealtime();
            reference.StepOnce();
        }
        double oldT = engine.T;
        DateTime oldLabel = engine.ToTime(oldT);
        clock.Advance(60.1);
        engine.StepRealtime();
        reference.StepOnce();

        Assert.Equal(reference.T, engine.T);
        Assert.Equal(referenceTarget.Snapshot().Pv, target.Snapshot().Pv);
        Assert.NotEqual(target.InitialPv, target.Pv);
        Assert.Equal(referenceTarget.Elapsed, target.Elapsed);
        Assert.Equal(oldLabel, engine.ToTime(oldT));
        Assert.Equal(engine.Now, engine.ToTime(engine.T));
        var interruption = Assert.Single(engine.Log.Snapshot(), x => x.Message.Contains("秒中断しました"));
        Assert.Equal(engine.Now, interruption.Time);
        Assert.Contains("60.0", interruption.Message);
        using var csv = new StringWriter();
        TrendCsv.Write(csv, target, [target.Trend[49], target.Trend[50]], engine.ToTime);
        Assert.Contains(oldLabel.ToString("yyyy/MM/dd HH:mm:ss.f"), csv.ToString());
        Assert.Contains(engine.Now.ToString("yyyy/MM/dd HH:mm:ss.f"), csv.ToString());
        clock.Advance(0.1);
        engine.StepRealtime();
        Assert.Single(engine.Log.Snapshot(), x => x.Message.Contains("秒中断しました"));
    }

    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void WallClockChangeDoesNotRelabelOldSamplesOrReportASuspension(int seconds)
    {
        var clock = new ManualClock();
        using var plc = new DummyPlc();
        using var engine = new SimulationEngine(plc, clock);
        clock.Advance(0.1);
        engine.StepRealtime();
        double oldT = engine.T;
        DateTime oldLabel = engine.ToTime(oldT);
        clock.Advance(0.1);
        clock.ChangeWall(seconds);
        engine.StepRealtime();
        Assert.Equal(oldLabel, engine.ToTime(oldT));
        Assert.Equal(engine.Now, engine.ToTime(engine.T));
        engine.Log.Add(null, "test", "wall clock");
        Assert.Equal(engine.Now, engine.Log.Snapshot()[^1].Time);
        Assert.DoesNotContain(engine.Log.Snapshot(), x => x.Message.Contains("秒中断しました"));
    }

    [Fact]
    public async Task CacheFreshnessAndPendingTimeoutUseElapsedTimeWhenClockMoves()
    {
        var clock = new ManualClock();
        using var client = new TestCache(clock);
        client.Connect();
        Assert.Equal(PlcIoStatus.Pending, client.Read("D0", "INT16", out _));
        await client.Poll();
        clock.ChangeWall(3600);
        Assert.Equal(PlcIoStatus.Ok, client.Read("D0", "INT16", out var value));
        Assert.Equal(42, value);
        clock.ChangeWall(-7200);
        clock.Advance(1);
        Assert.Equal(PlcIoStatus.Failed, client.Read("D0", "INT16", out _));
        Assert.Equal(PlcIoStatus.Pending, client.Read("D2", "INT16", out _));
        clock.Advance(1);
        Assert.Equal(PlcIoStatus.Failed, client.Read("D2", "INT16", out _));
    }

    [Fact]
    public async Task CacheRetriesBadPointsAndExpiresUnusedPointsWithoutWallClockDependence()
    {
        var clock = new ManualClock();
        using var client = new TestCache(clock) { FailPoint = true };
        client.Connect();
        client.Read("D0", "INT16", out _);
        await client.Poll();
        Assert.Equal(PlcIoStatus.Failed, client.Read("D0", "INT16", out _));
        int attempts = client.ReadAttempts;
        clock.ChangeWall(3600);
        await client.Poll();
        Assert.Equal(attempts, client.ReadAttempts);
        clock.ChangeWall(-7200);
        clock.Advance(2.1);
        client.FailPoint = false;
        await client.Poll();
        Assert.Equal(PlcIoStatus.Ok, client.Read("D0", "INT16", out _));
        clock.Advance(5.1);
        await client.Poll();
        Assert.Equal(PlcIoStatus.Pending, client.Read("D0", "INT16", out _));
    }

    private static ControlTarget Target() => new("test", ModelKind.Flow, seed: 1)
    { MvAddress = "D0", PvAddress = "D2", UseSp = false };

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        private long _utcTicks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        public void Advance(double seconds)
        {
            long ticks = TimeSpan.FromSeconds(seconds).Ticks;
            Interlocked.Add(ref _timestamp, ticks);
            Interlocked.Add(ref _utcTicks, ticks);
        }
        public void ChangeWall(double seconds) => Interlocked.Add(ref _utcTicks, TimeSpan.FromSeconds(seconds).Ticks);
    }

    private sealed class Connection : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestCache(TimeProvider clock) : CachedPlcClient<Connection>(
        new PlcSettings { TimeoutMs = 100, CommCycleMs = 20 }, "test", clock)
    {
        public bool FailPoint { get; set; }
        public int ReadAttempts { get; private set; }
        // Hold the background connection so each exchange is driven deterministically by the test.
        protected override async Task<Connection> OpenClientAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new Connection();
        }
        public Task Poll() => ExchangeAsync(new Connection(), CancellationToken.None);
        protected override Task<object> ReadValueAsync(Connection client, string address, string suffix, CancellationToken ct)
        {
            ReadAttempts++;
            if (FailPoint) throw new ArgumentException("bad point");
            return Task.FromResult<object>((short)42);
        }
        protected override async Task<IReadOnlyDictionary<string, object>> ReadValuesAsync(Connection client, string[] keys, CancellationToken ct)
        {
            var result = new Dictionary<string, object>();
            foreach (string key in keys) result[key] = await ReadValueAsync(client, key, "", ct);
            return result;
        }
        protected override Task WriteValueAsync(Connection client, string address, string suffix, double raw, CancellationToken ct) => Task.CompletedTask;
        protected override Task WriteValuesAsync(Connection client, IReadOnlyDictionary<string, object> updates, CancellationToken ct) => Task.CompletedTask;
        protected override bool IsPointError(Exception error) => error is ArgumentException;
        protected override string DescribeError(Exception error) => error.Message;
    }
}
