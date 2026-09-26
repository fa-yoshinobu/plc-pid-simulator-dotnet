namespace PidSimulator.Core;

public enum DisturbanceScheduleState { None, Waiting, Active, Completed, Cancelled }

public sealed record DisturbanceScheduleSnapshot(
    DisturbanceScheduleState State, double Value, double SecondsUntilStart, double? SecondsRemaining);

/// <summary>対象の演算経過時間で動く単発の外乱。壁時計やバックグラウンドタイマーは使わない。</summary>
internal sealed class DisturbanceSchedule
{
    // 経過時間の加算誤差で、ちょうどの境界が1周期遅れないようにする。
    private const double BoundaryTolerance = 1e-9;
    public DisturbanceScheduleState State { get; private set; }
    public double Value { get; private set; }
    private double _start;
    private double? _end;

    public bool IsScheduled => State is DisturbanceScheduleState.Waiting or DisturbanceScheduleState.Active;

    public void Schedule(double elapsed, double value, double delay, double? duration)
    {
        Value = value;
        _start = elapsed + delay;
        _end = duration is { } seconds ? _start + seconds : null;
        State = DisturbanceScheduleState.Waiting;
    }

    public void Cancel() => State = DisturbanceScheduleState.Cancelled;

    public void Advance(double elapsed)
    {
        if (!IsScheduled) return;
        if (_end is { } end && elapsed + BoundaryTolerance >= end)
            State = DisturbanceScheduleState.Completed;
        else if (elapsed + BoundaryTolerance >= _start)
            State = DisturbanceScheduleState.Active;
    }

    public DisturbanceScheduleSnapshot Snapshot(double elapsed) => new(
        State, Value,
        State == DisturbanceScheduleState.Waiting ? Math.Max(0, _start - elapsed) : 0,
        IsScheduled && _end is { } end ? Math.Max(0, end - Math.Max(elapsed, _start)) : null);
}
