using System.Diagnostics;
using PidSimulator.Core.Plc;

namespace PidSimulator.Core;

/// <summary>
/// 登録された全制御対象を専用スレッドで周期演算する。
/// UIはこのスレッドに触れず、ControlTarget.Snapshot() とトレンドのコピーで表示する。
/// </summary>
public sealed class SimulationEngine : IDisposable
{
    public const double Dt = 0.1;

    private readonly object _listLock = new();
    private readonly List<ControlTarget> _targets = [];
    private Thread? _thread;
    private volatile bool _running;
    private double _t;
    private int _trendMinutes = TrendBuffer.DefaultRetentionMinutes;

    public SimulationEngine(IPlcClient plc)
    {
        _plc = plc;
        Log.Clock = () => Now;
    }

    private IPlcClient _plc;

    public IPlcClient Plc => Volatile.Read(ref _plc);

    /// <summary>通信方式を切り替える。すべての対象を停止してから呼ぶこと。古いクライアントは呼び出し側で破棄する。</summary>
    public IPlcClient SwapPlc(IPlcClient next)
    {
        if (Targets.Any(t => t.RunState != RunState.Stopped))
            throw new InvalidOperationException("制御中の対象があるため通信方式を切り替えられません。");
        var old = Interlocked.Exchange(ref _plc, next);
        Log.Add(null, "通信", $"PLC通信を切替: {next.Endpoint}");
        return old;
    }
    public EventLog Log { get; } = new();

    /// <summary>シミュレーション時刻 0 に対応する実時刻</summary>
    public DateTime BaseTime { get; set; } = DateTime.Now;

    /// <summary>シミュレーション経過時間 [s]</summary>
    public double T => Volatile.Read(ref _t);

    public DateTime Now => BaseTime.AddSeconds(T);

    public DateTime ToTime(double t) => BaseTime.AddSeconds(t);

    public IReadOnlyList<ControlTarget> Targets
    {
        get { lock (_listLock) return _targets.ToArray(); }
    }

    public void Add(ControlTarget target)
    {
        lock (_listLock)
        {
            target.SetTrendRetention(_trendMinutes);
            _targets.Add(target);
        }
        Log.Add(target, "登録", "制御対象を登録");
    }

    public void Remove(ControlTarget target)
    {
        lock (_listLock) _targets.Remove(target);
        Log.Add(null, "登録", $"{target.Name} を削除");
    }

    /// <summary>登録内容の変更でモデル種類が変わったときに、同じ位置へ差し替える</summary>
    public void Replace(ControlTarget oldTarget, ControlTarget newTarget)
    {
        lock (_listLock)
        {
            newTarget.SetTrendRetention(_trendMinutes);
            int i = _targets.IndexOf(oldTarget);
            if (i < 0) _targets.Add(newTarget);
            else _targets[i] = newTarget;
        }
        Log.Add(newTarget, "登録", "登録内容を編集（モデル変更）");
    }

    public int TrendMinutes { get { lock (_listLock) return _trendMinutes; } }

    /// <summary>既存対象と今後登録する対象へ共通の保持時間を適用する。短縮時は最新の履歴を残す。</summary>
    public void SetTrendRetention(int minutes)
    {
        TrendBuffer.CapacityForMinutes(minutes);
        lock (_listLock)
        {
            foreach (var target in _targets) target.SetTrendRetention(minutes);
            _trendMinutes = minutes;
        }
    }

    /// <summary>プロジェクトの新規作成・読込時に全対象を外す。呼び出し前に停止しておくこと。</summary>
    public void Clear()
    {
        lock (_listLock) _targets.Clear();
    }

    /// <summary>1周期分を同期実行する（テストとウォームアップ用）。</summary>
    public void StepOnce()
    {
        var list = Targets;
        double t = T + Dt;
        Volatile.Write(ref _t, t);
        var plc = Plc;
        plc.Tick(Dt);
        foreach (var target in list) target.Cycle(plc, Dt, t, Log);
    }

    public void Run()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "SimulationEngine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        double next = 0;
        while (_running)
        {
            StepOnce();
            next += Dt * 1000;
            double wait = next - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -1000) next = sw.Elapsed.TotalMilliseconds; // 大幅な遅れは追いつかずに捨てる
        }
    }

    /// <summary>全制御停止（仕様 §21）。プレビューも終了する。</summary>
    public int StopAll(string reason)
    {
        int n = 0;
        foreach (var t in Targets)
        {
            if (t.RunState == RunState.Preview) { t.SetPreview(false, Log); n++; }
            else if (t.Stop(Log, reason)) n++;
        }
        Log.Add(null, "運転", $"全制御停止（{n} 件）");
        return n;
    }

    public int ReleaseAllForces()
    {
        int n = Targets.Sum(t => t.ReleaseForces(Log, "全FORCE解除"));
        if (n > 0) Log.Add(null, "FORCE", $"全FORCE解除（{n} 件）");
        return n;
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);
    }
}
