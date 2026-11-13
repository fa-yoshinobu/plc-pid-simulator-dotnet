namespace PidSimulator.Core;

public sealed record EventEntry(DateTime Time, Guid? TargetId, string TargetName, string Category, string Message);

/// <summary>イベントログ（仕様 §16）。どのスレッドからも追加できる。</summary>
public sealed class EventLog
{
    public const int Capacity = 5000;
    private readonly object _lock = new();
    private readonly List<EventEntry> _items = [];

    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>追加元のスレッドで発火する。UI側でディスパッチすること。</summary>
    public event Action<EventEntry>? Added;

    /// <summary>保持上限に達した時点で一度発火する。追加元のスレッドで発火するため、UI側でディスパッチすること。</summary>
    public event Action? RetentionWarning;

    public void Add(ControlTarget? target, string category, string message)
    {
        var e = new EventEntry(Clock(), target?.Id, target?.Name ?? "システム", category, message);
        bool notifyRetention;
        lock (_lock)
        {
            _items.Add(e);
            notifyRetention = _items.Count == Capacity;
            if (_items.Count > Capacity) _items.RemoveAt(0);
        }
        if (notifyRetention) RetentionWarning?.Invoke();
        Added?.Invoke(e);
    }

    public IReadOnlyList<EventEntry> Snapshot()
    {
        lock (_lock) return _items.ToArray();
    }
}
