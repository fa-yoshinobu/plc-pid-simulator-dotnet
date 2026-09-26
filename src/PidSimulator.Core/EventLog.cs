namespace PidSimulator.Core;

public sealed record EventEntry(DateTime Time, Guid? TargetId, string TargetName, string Category, string Message);

/// <summary>イベントログ（仕様 §16）。どのスレッドからも追加できる。</summary>
public sealed class EventLog
{
    private readonly object _lock = new();
    private readonly List<EventEntry> _items = [];

    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>追加元のスレッドで発火する。UI側でディスパッチすること。</summary>
    public event Action<EventEntry>? Added;

    public void Add(ControlTarget? target, string category, string message)
    {
        var e = new EventEntry(Clock(), target?.Id, target?.Name ?? "システム", category, message);
        lock (_lock)
        {
            _items.Add(e);
            if (_items.Count > 5000) _items.RemoveAt(0);
        }
        Added?.Invoke(e);
    }

    public IReadOnlyList<EventEntry> Snapshot()
    {
        lock (_lock) return _items.ToArray();
    }
}
