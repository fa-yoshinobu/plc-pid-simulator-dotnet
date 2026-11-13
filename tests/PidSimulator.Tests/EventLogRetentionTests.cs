using PidSimulator.Core;

namespace PidSimulator.Tests;

public class EventLogRetentionTests
{
    [Fact]
    public void WarnsOnceAtCapacityBeforeSequentialAddsRemoveTheOldestEntry()
    {
        var log = new EventLog();
        int warnings = 0;
        log.RetentionWarning += () =>
        {
            warnings++;
            Assert.Equal(EventLog.Capacity, log.Snapshot().Count);
            Assert.Equal("0", log.Snapshot()[0].Message);
        };
        for (int i = 0; i < EventLog.Capacity - 1; i++) log.Add(null, "test", i.ToString());
        Assert.Equal(0, warnings);
        log.Add(null, "test", (EventLog.Capacity - 1).ToString());
        Assert.Equal(1, warnings);
        for (int i = EventLog.Capacity; i < EventLog.Capacity + 200; i++) log.Add(null, "test", i.ToString());
        var entries = log.Snapshot();
        Assert.Equal(EventLog.Capacity, entries.Count);
        Assert.Equal("200", entries[0].Message);
        Assert.Equal((EventLog.Capacity + 199).ToString(), entries[^1].Message);
        Assert.Equal(1, warnings);
    }

    [Fact]
    public void RetentionSubscriberCanReadTheLogFromAnotherThread()
    {
        var log = new EventLog();
        bool called = false;
        log.RetentionWarning += () =>
        {
            called = true;
            IReadOnlyList<EventEntry>? snapshot = null;
            var reader = new Thread(() => snapshot = log.Snapshot()) { IsBackground = true };
            reader.Start();
            Assert.True(reader.Join(TimeSpan.FromSeconds(2)), "Retention subscribers must run outside the event-log lock.");
            Assert.NotNull(snapshot);
            Assert.Equal(EventLog.Capacity, snapshot.Count);
        };
        for (int i = 0; i < EventLog.Capacity; i++) log.Add(null, "test", i.ToString());
        Assert.True(called);
    }

    [Fact]
    public void ConcurrentAddsKeepTheLimitAndNotifyOnlyOnce()
    {
        var log = new EventLog();
        int warnings = 0;
        log.RetentionWarning += () => Interlocked.Increment(ref warnings);
        Parallel.For(0, EventLog.Capacity * 2, i => log.Add(null, "test", i.ToString()));
        Assert.Equal(EventLog.Capacity, log.Snapshot().Count);
        Assert.Equal(1, warnings);
        Assert.Equal(EventLog.Capacity, log.Snapshot().Select(e => e.Message).Distinct().Count());
    }
}
