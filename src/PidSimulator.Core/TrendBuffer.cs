namespace PidSimulator.Core;

public readonly record struct TrendSample(double T, double Sp, double Pv, double Mv, double Dist, bool Forced, bool CommOk = true);

/// <summary>固定長リングバッファ。呼び出し側（ControlTarget.Sync）でロックする。</summary>
public sealed class TrendBuffer
{
    public const int MinRetentionMinutes = 1;
    public const int MaxRetentionMinutes = 240;
    public const int DefaultRetentionMinutes = 30;

    private TrendSample[] _buf;
    private int _start;

    public TrendBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _buf = new TrendSample[capacity];
    }

    public static int CapacityForMinutes(int minutes)
    {
        if (minutes is < MinRetentionMinutes or > MaxRetentionMinutes)
            throw new ArgumentOutOfRangeException(nameof(minutes), $"トレンド保持時間は {MinRetentionMinutes}～{MaxRetentionMinutes} 分で指定してください。");
        return checked(minutes * 600); // 演算周期 100 ms
    }

    public int Count { get; private set; }
    public int Capacity => _buf.Length;

    /// <summary>最新の履歴を残して保持点数を変更する。呼び出し側で ControlTarget.Sync をロックする。</summary>
    public void Resize(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (capacity == Capacity) return;
        var next = new TrendSample[capacity];
        int keep = Math.Min(Count, capacity);
        for (int i = 0; i < keep; i++) next[i] = this[Count - keep + i];
        _buf = next;
        _start = 0;
        Count = keep;
    }

    public void Add(in TrendSample s)
    {
        _buf[(_start + Count) % _buf.Length] = s;
        if (Count < _buf.Length) Count++;
        else _start = (_start + 1) % _buf.Length;
    }

    public void Clear()
    {
        _start = 0;
        Count = 0;
    }

    public TrendSample this[int i] => _buf[(_start + i) % _buf.Length];

    public void CopyRange(double t0, double t1, List<TrendSample> dest)
    {
        for (int i = 0; i < Count; i++)
        {
            var s = this[i];
            if (s.T < t0) continue;
            if (s.T > t1) break;
            dest.Add(s);
        }
    }
}
