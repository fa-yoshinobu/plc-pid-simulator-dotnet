namespace PidSimulator.Core;

public sealed class ForceItem
{
    public bool On { get; set; }
    public double Value { get; set; }
}

public sealed class ForceSet
{
    private readonly Dictionary<ForceKey, ForceItem> _items =
        Enum.GetValues<ForceKey>().ToDictionary(k => k, _ => new ForceItem());

    public ForceItem this[ForceKey k] => _items[k];
    public bool Any => _items.Values.Any(i => i.On);
    public ForceKey[] Active => _items.Where(p => p.Value.On).Select(p => p.Key).ToArray();


    internal double? ActiveValue(ForceKey key) => this[key].On ? this[key].Value : null;

    internal void Set(ForceKey key, bool on, double value, ControlTarget target, EventLog log)
    {
        var force = this[key];
        bool changed = force.On != on || (on && force.Value != value);
        force.Value = value;
        force.On = on;
        if (changed)
            log.Add(target, "FORCE", on ? $"{Labels.Of(key)} FORCE ON {value:0.0##}" : $"{Labels.Of(key)} FORCE OFF");
    }

    internal int ReleaseAll(ControlTarget target, EventLog log, string? reason)
    {
        int count = 0;
        foreach (var key in Active)
        {
            this[key].On = false;
            count++;
            log.Add(target, "FORCE", $"{Labels.Of(key)} FORCE OFF{(reason != null ? $"（{reason}）" : "")}");
        }
        return count;
    }
}
