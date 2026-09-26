namespace PidSimulator.Core.Models;

// 仕様 §10 の基本プロセスモデルとポンプ流量モデル。
// 時定数はPID調整の試験で待ち時間が長くなりすぎないよう、実設備より短めの標準値にしている。

/// <summary>§10.1 モーター回転数：簡易応答または慣性とトルク制限付きの速度ドライブ。</summary>
public sealed class MotorModel(ModelInfo info, int seed) : ProcessModel(info, seed)
{
    protected override double Integrate(double u, double d, double dt, double t)
    {
        if (Par("motorMode") == 1) return IntegrateInertia(u, d, dt);
        double load = InputOverride ?? Par("load");
        double target = Par("nmax") * u / 100 * Math.Clamp(1 - (load + d) / 250, 0, 1);
        double limit = Par("acc") * dt;
        double x = X + Math.Clamp(FirstOrder(X, target, Par("tau"), dt) - X, -limit, limit);
        return Math.Clamp(x, 0, Par("nmax"));
    }

    private double IntegrateInertia(double u, double d, double dt)
    {
        const double radiansPerRpm = Math.PI / 30;
        double maximum = Par("nmax") * radiansPerRpm;
        double command = maximum * u / 100;
        double band = maximum * Par("speedband") / 100;
        double torque = Par("torque"), gain = torque / band;
        double inertia = Par("moment"), damping = Par("damping");
        double load = torque * Math.Max(0, (InputOverride ?? Par("load")) + d) / 100;
        double speed = Math.Clamp(X * radiansPerRpm, 0, maximum);
        // J·dω/dt = clamp(K(ωcmd−ω), ±Tmax) − bω − Tload.
        // トルクの飽和境界をまたぐ時間を解く。大きい演算刻みでも定常回転数を飛び越さない。
        for (int region = 0; region < 3 && dt > 0; region++)
        {
            double drive = Math.Clamp(gain * (command - speed), -torque, torque);
            double rate = (drive - damping * speed - load) / inertia;
            bool belowBand = speed < command - band || (speed == command - band && rate < 0);
            bool aboveBand = speed > command + band || (speed == command + band && rate > 0);
            double decay = (damping + (!belowBand && !aboveBand ? gain : 0)) / inertia;
            double next = LinearStep(speed, rate, decay, dt);
            double boundary = rate > 0
                ? belowBand ? command - band : aboveBand ? double.PositiveInfinity : command + band
                : aboveBand ? command + band : belowBand ? double.NegativeInfinity : command - band;
            if (rate == 0 || !double.IsFinite(boundary) || (rate > 0 ? next <= boundary : next >= boundary))
                return Math.Clamp(next / radiansPerRpm, 0, Par("nmax"));
            dt -= Math.Clamp(BoundaryTime(speed, rate, decay, boundary), 0, dt);
            speed = boundary;
        }
        return Math.Clamp(speed / radiansPerRpm, 0, Par("nmax"));
    }
}

/// <summary>§10.2 流量：弁特性・配管抵抗・圧力変動</summary>
public sealed class FlowModel(ModelInfo info, int seed) : ProcessModel(info, seed)
{
    protected override double Integrate(double u, double d, double dt, double t)
    {
        double pressureRatio = (InputOverride ?? 100) / 100 * (1 - d / 100)
                               * (1 + Par("pvar") / 100 * Math.Sin(t * 0.7));
        double target = Par("qmax") * Math.Sqrt(Math.Max(0, pressureRatio)) * Valve(u / 100, Par("char"))
                        / Math.Sqrt(1 + Par("res") / 100);
        return Math.Max(0, FirstOrder(X, target, Par("tau"), dt));
    }
}

/// <summary>遠心ポンプの回転数制御。相似則と、静揚程＋流量二乗の抵抗を合成した簡易モデル。</summary>
public sealed class PumpFlowModel(ModelInfo info, int seed) : ProcessModel(info, seed)
{
    protected override double Integrate(double u, double d, double dt, double t)
    {
        double headRatio = (InputOverride ?? 100) / 100 * (1 - d / 100)
                           * (1 + Par("pvar") / 100 * Math.Sin(t * 0.7));
        double speed = u / 100;
        double shutoffHead = Par("hmax"), staticHead = Par("hstatic");
        // H0*n² = Hstatic + K*Q² とし、100%回転数時の基準流量からKを校正する。
        // 低速では静揚程に届くまで流量0。逆流は扱わない。
        double availableHead = Math.Max(0, shutoffHead * headRatio * speed * speed - staticHead);
        double target = shutoffHead > staticHead
            ? Par("qmax") * Math.Sqrt(availableHead / (shutoffHead - staticHead))
              / Math.Sqrt(1 + Par("res") / 100)
            : 0;
        return Math.Max(0, FirstOrder(X, target, Par("tau"), dt));
    }
}

/// <summary>§10.3 液面：A·dH/dt = Qin − Qout、自然排水 Qout = k√H</summary>
public sealed class LevelModel(ModelInfo info, int seed) : ProcessModel(info, seed)
{
    private double _valve;

    protected override void ResetState() => _valve = 0;

    protected override double Integrate(double u, double d, double dt, double t)
    {
        _valve = FirstOrder(_valve, u, Par("valve"), dt);
        double kout = Par("kout") * Math.Max(0, InputOverride ?? 50) / 50;
        double qin = Par("qin") * _valve / 100;
        double capacity = TankGeometry.CapacityCubicMetres(P);
        double scale = dt / 3600 / capacity * 100;
        // 水収支を陰的に解く。y=√液位として y² + (scale·kout)y = X + scale·(qin-d)。
        // 小タンクでも排水量の計算が液位を飛び越して数値振動しない。
        double available = Math.Max(0, X + scale * (qin - d));
        double drain = scale * kout;
        double levelRoot = available == 0 ? 0 : 2 * available / (Math.Sqrt(drain * drain + 4 * available) + drain);
        return Math.Clamp(levelRoot * levelRoot, 0, 100);
    }
}

/// <summary>§10.4 電気ヒーター：熱容量・放熱</summary>
public sealed class HeaterModel(ModelInfo info, int seed) : ProcessModel(info, seed)
{
    protected override double Integrate(double u, double d, double dt, double t)
    {
        double amb = InputOverride ?? Par("amb");
        double q = Par("pmax") * u / 100 - Par("hloss") * (X - amb) - d;
        return LinearStep(X, q / Par("cap"), Par("hloss") / Par("cap"), dt);
    }
}

/// <summary>§10.5 蒸気ジャケット・コイル：供給能力と温度差伝熱の小さい方で加熱する。</summary>
public sealed class SteamModel(ModelInfo info, int seed) : ProcessModel(info, seed)
{
    protected override double Integrate(double u, double d, double dt, double t)
    {
        double opening = u < Par("db") ? 0 : Valve(u / 100, Par("char"));
        double factor = opening * Math.Max(0, InputOverride ?? 100) / 100;
        double cap = Par("cap"), loss = Par("hloss"), load = Par("load") + d;
        double ua = Par("ua"), steam = Par("ts"), maxPower = Par("pmax");
        double x = X;
        if (factor <= 0 || ua <= 0 || maxPower <= 0)
            return LinearStep(x, (-loss * (x - Par("amb")) - load) / cap, loss / cap, dt);

        double limitTemperature = steam - maxPower / ua;
        // 供給能力制限／温度差伝熱／蒸気温度以上の3領域はそれぞれ線形。
        // 境界までの時間を解いて残り時間を次の領域で進めるので、刻みが大きくても飛び越さない。
        for (int region = 0; region < 3 && dt > 0; region++)
        {
            double heat = factor * Math.Min(maxPower, ua * Math.Max(steam - x, 0));
            double rate = (heat - loss * (x - Par("amb")) - load) / cap;
            bool belowLimit = x < limitTemperature || (x == limitTemperature && rate < 0);
            bool belowSteam = x < steam || (x == steam && rate < 0);
            double decay = (loss + (!belowLimit && belowSteam ? factor * ua : 0)) / cap;
            double next = LinearStep(x, rate, decay, dt);
            double boundary = rate > 0
                ? (belowLimit ? limitTemperature : belowSteam ? steam : double.PositiveInfinity)
                : (belowLimit ? double.NegativeInfinity : belowSteam ? limitTemperature : steam);
            if (rate == 0 || !double.IsFinite(boundary) || (rate > 0 ? next <= boundary : next >= boundary))
                return next;

            double crossingTime = BoundaryTime(x, rate, decay, boundary);
            dt -= Math.Clamp(crossingTime, 0, dt);
            x = boundary;
        }
        return x;
    }
}

/// <summary>§10.6 チラー：冷却応答・最低到達温度（逆動作）</summary>
public sealed class ChillerModel(ModelInfo info, int seed) : ProcessModel(info, seed)
{
    private double _cool;

    protected override void ResetState() => _cool = 0;

    protected override double Integrate(double u, double d, double dt, double t)
    {
        _cool = FirstOrder(_cool, u, Par("resp"), dt);
        double load = InputOverride ?? Par("load");
        double cooling = Par("cmax") * _cool / 100, cap = Par("cap"), floor = Par("tmin");
        double decay = Par("hloss") / cap;
        double Passive(double x) => (load + d - Par("hloss") * (x - Par("amb"))) / cap;
        // 最低到達温度は冷却装置だけの限界。既に低温の水を瞬間的に昇温させず、周囲との熱移動は続く。
        double rate = X < floor ? Passive(X) : Passive(X) - cooling / cap;
        if (X == floor && rate < 0) rate = Math.Min(0, Passive(X));
        double next = LinearStep(X, rate, decay, dt);
        if (X < floor ? next <= floor : next >= floor) return next;
        if (X == floor) return next;

        double remaining = dt - Math.Clamp(BoundaryTime(X, rate, decay, floor), 0, dt);
        double floorRate = Passive(floor);
        if (floorRate >= 0) floorRate = Math.Max(0, floorRate - cooling / cap);
        return LinearStep(floor, floorRate, decay, remaining);
    }
}

/// <summary>§10.7 圧力：供給・排出・漏れ</summary>
public sealed class PressureModel(ModelInfo info, int seed) : ProcessModel(info, seed)
{
    private double _supply;

    protected override void ResetState() => _supply = 0;

    protected override double Integrate(double u, double d, double dt, double t)
    {
        _supply = FirstOrder(_supply, u, Par("resp"), dt);
        double qout = InputOverride ?? Par("qout");
        double q = Par("qsup") * _supply / 100 - Par("leak") * X / 100 - qout - d;
        // Nm³は0℃・101.325 kPa(abs)での体積。PVは大気圧を0とするゲージ圧。
        double coefficient = 101.325 * (Par("temp") + 273.15) / 273.15 / 3600 / Par("vol");
        return Math.Max(0, LinearStep(X, q * coefficient, Par("leak") / 100 * coefficient, dt));
    }
}
