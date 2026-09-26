namespace PidSimulator.Core.Models;

/// <summary>
/// プロセスモデルの基底。むだ時間・センサ遅れ・ノイズを共通で扱い、
/// 派生クラスは <see cref="Integrate"/> で真値の変化だけを計算する。
/// </summary>
public abstract class ProcessModel
{
    private readonly Queue<(double Time, double Mv)> _deadTime = new();
    private readonly Random _rnd;
    private double _inputTime;
    private double _delayedMv;

    protected ProcessModel(ModelInfo info, int seed)
    {
        Info = info;
        P = info.Params.ToDictionary(p => p.Key, p => p.Default);
        _rnd = seed == 0 ? new Random() : new Random(seed);
    }

    public ModelInfo Info { get; }

    /// <summary>モデルパラメータ（キーは ParamDef.Key）</summary>
    public Dictionary<string, double> P { get; }

    /// <summary>プロセスの真値</summary>
    public double X { get; protected set; }

    /// <summary>センサ遅れ後の値</summary>
    public double Measured { get; private set; }

    /// <summary>ノイズを含むセンサ出力（PVとして書き込む値）</summary>
    public double Output { get; private set; }

    /// <summary>モデル固有入力 FORCE（null で通常値）</summary>
    public double? InputOverride { get; set; }

    public void Reset(double initial)
    {
        X = Measured = Output = initial;
        _deadTime.Clear();
        _inputTime = _delayedMv = 0;
        ResetState();
    }

    protected virtual void ResetState() { }

    public double Step(double mv, double dist, double dt, double t)
    {
        if (!double.IsFinite(dt) || dt <= 0) throw new ArgumentOutOfRangeException(nameof(dt));
        // 開始前の操作量は0%。開始直後も、指定したむだ時間が経つまで操作を伝えない。
        // 演算周期より細かい遅れは、次の演算時点で反映する。
        _deadTime.Enqueue((_inputTime, Math.Clamp(mv, 0, 100)));
        double delayedTime = _inputTime - Par("dead");
        while (_deadTime.TryPeek(out var input) && input.Time <= delayedTime + 1e-10)
        {
            _delayedMv = _deadTime.Dequeue().Mv;
        }
        _inputTime += dt;

        X = Integrate(_delayedMv, dist, dt, t);

        Measured = FirstOrder(Measured, X, Par("sens"), dt);
        double noise = (_rnd.NextDouble() - 0.5) * 2 * Par("noise") / 100 * Info.Span;
        Output = Measured + noise;
        return Output;
    }

    /// <param name="u">むだ時間後のMV [%]</param>
    /// <param name="d">外乱量（モデル固有単位）</param>
    protected abstract double Integrate(double u, double d, double dt, double t);

    protected double Par(string key) => P.TryGetValue(key, out var v) ? v : 0;

    /// <summary>周期内の入力を一定とした一次遅れの厳密解。短い時定数でも数値振動しない。</summary>
    protected static double FirstOrder(double value, double target, double tau, double dt)
    {
        if (tau <= 0) return target;
        double z = dt / tau;
        double weight = z < 1e-6 ? z * (1 - z / 2 + z * z / 6) : 1 - Math.Exp(-z);
        return value + (target - value) * weight;
    }

    /// <summary>x' = rate − decay・(x − 現在値) の厳密解。</summary>
    protected static double LinearStep(double value, double rate, double decay, double dt)
    {
        double z = decay * dt;
        double factor = Math.Abs(z) < 1e-6
            ? dt * (1 - z / 2 + z * z / 6)
            : (1 - Math.Exp(-z)) / decay;
        return value + rate * factor;
    }

    /// <summary>線形応答が境界値に達する時間。呼出側で実際に境界を通過することを確認する。</summary>
    protected static double BoundaryTime(double value, double rate, double decay, double boundary)
    {
        double distanceTime = (boundary - value) / rate;
        double ratio = decay * distanceTime;
        return Math.Abs(ratio) < 1e-6
            ? distanceTime * (1 + ratio / 2 + ratio * ratio / 3)
            : -Math.Log(1 - ratio) / decay;
    }

    protected static double Valve(double u01, double characteristic)
    {
        u01 = Math.Clamp(u01, 0, 1);
        return (int)characteristic switch
        {
            1 => (Math.Pow(30, u01) - 1) / 29,
            2 => Math.Sqrt(u01),
            _ => u01,
        };
    }
}
