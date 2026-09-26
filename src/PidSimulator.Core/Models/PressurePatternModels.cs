namespace PidSimulator.Core.Models;

/// <summary>理想空気（比熱比1.4）の弁流量。Nm³/hの能力を20℃の基準圧力条件から換算する。</summary>
public static class AirValveFlow
{
    public const double AtmosphericPressure = 101.325;
    public const double ReferenceTemperatureKelvin = 293.15;
    private const double Gamma = 1.4;
    private static readonly double CriticalRatio = Math.Pow(2 / (Gamma + 1), Gamma / (Gamma - 1));

    /// <summary>基準は指定した入口ゲージ圧から大気への排出。圧力はkPa(g)、温度は℃。</summary>
    public static double Ratio(double upstreamGauge, double downstreamGauge, double referenceUpstreamGauge, double temperatureC)
    {
        double reference = Capacity(referenceUpstreamGauge, 0);
        if (reference <= 0) return 0;
        return Capacity(upstreamGauge, downstreamGauge) / reference
               * Math.Sqrt(ReferenceTemperatureKelvin / (temperatureC + 273.15));
    }

    private static double Capacity(double upstreamGauge, double downstreamGauge)
    {
        if (upstreamGauge <= downstreamGauge || upstreamGauge <= 0) return 0;
        double upstream = upstreamGauge + AtmosphericPressure;
        double ratio = Math.Clamp((Math.Max(0, downstreamGauge) + AtmosphericPressure) / upstream, CriticalRatio, 1);
        // 臨界圧力比以下はチョークする。係数2γ/(γ−1)は基準との比で相殺される。
        double function = Math.Pow(ratio, 2 / Gamma) - Math.Pow(ratio, (Gamma + 1) / Gamma);
        return upstream * Math.Sqrt(Math.Max(0, function));
    }
}

internal static class IsothermalAirTank
{
    public static double PressurePerNormalFlow(double volume, double temperatureC) =>
        AirValveFlow.AtmosphericPressure * (temperatureC + 273.15) / 273.15 / 3600 / volume;

    // netFlow(P)が単調減少する等温タンクを後退Eulerで解く。
    // 巨大dtや小容量でも平衡点を飛び越さず、気体量不足では0 kPa(g)に制限する。
    public static double Advance(double initial, double scale, double upperBound, Func<double, double> netFlow)
    {
        initial = Math.Max(0, initial);
        double initialFlow = netFlow(initial);
        if (initialFlow == 0) return initial;
        double inverseScale = 1 / scale;
        if (scale <= 0 || double.IsPositiveInfinity(inverseScale)) return initial;
        upperBound = double.IsFinite(upperBound) ? Math.Max(0, upperBound) : double.MaxValue;
        double Residual(double pressure) => (initial - pressure) * inverseScale + netFlow(pressure);
        if (Residual(0) <= 0) return 0;

        double lower = initialFlow > 0 ? initial : 0;
        double upper = initialFlow < 0 ? initial : Math.Min(upperBound, Math.Max(1, initial));
        while (upper < upperBound && Residual(upper) > 0)
            upper = Math.Min(upperBound, upper * 2);

        for (int i = 0; i < 80; i++)
        {
            double middle = lower + (upper - lower) / 2;
            if (Residual(middle) > 0) lower = middle;
            else upper = middle;
            if (upper - lower <= 1e-12 * Math.Max(1, upper)) break;
        }
        return lower + (upper - lower) / 2;
    }
}

/// <summary>給気弁を操作する空気圧タンク。供給圧との圧力比で弁流量を計算する。</summary>
public sealed class PressureSupplyValveModel(ModelInfo info, int seed) : ProcessModel(info, seed)
{
    private double _opening;
    protected override void ResetState() => _opening = 0;

    protected override double Integrate(double u, double d, double dt, double t)
    {
        _opening = FirstOrder(_opening, u, Par("resp"), dt);
        double valve = Valve(_opening / 100, Par("char"));
        double source = Math.Max(0, InputOverride ?? Par("psupply"));
        double consumption = Math.Max(0, Par("qout") + d);
        double Flow(double pressure) => Par("qmax") * valve
            * AirValveFlow.Ratio(source, pressure, Par("psupply"), Par("temp"))
            - consumption - Par("leak") * pressure / 100;
        double scale = dt * IsothermalAirTank.PressurePerNormalFlow(Par("vol"), Par("temp"));
        return IsothermalAirTank.Advance(X, scale, Math.Max(X, source), Flow);
    }
}

/// <summary>排気弁を操作する空気圧タンク。一定供給量と大気への排気量の収支を計算する。</summary>
public sealed class PressureExhaustValveModel(ModelInfo info, int seed) : ProcessModel(info, seed)
{
    private double _opening;
    protected override void ResetState() => _opening = 0;

    protected override double Integrate(double u, double d, double dt, double t)
    {
        _opening = FirstOrder(_opening, u, Par("resp"), dt);
        double valve = Valve(_opening / 100, Par("char"));
        double supply = Math.Max(0, (InputOverride ?? Par("qsup")) + d);
        double Flow(double pressure) => supply - Par("qmax") * valve
            * AirValveFlow.Ratio(pressure, 0, 100, Par("temp"))
            - Par("leak") * pressure / 100;
        double scale = dt * IsothermalAirTank.PressurePerNormalFlow(Par("vol"), Par("temp"));
        double upper = Math.Max(0, X) + scale * supply;
        return IsothermalAirTank.Advance(X, scale, upper, Flow);
    }
}

/// <summary>遠心ポンプの吐出圧。ポンプ曲線と需要側の二次抵抗の交点へ一次遅れで追従する。</summary>
public sealed class PumpPressureModel(ModelInfo info, int seed) : ProcessModel(info, seed)
{
    protected override double Integrate(double u, double d, double dt, double t)
    {
        double demand = Math.Max(0, (InputOverride ?? Par("qout")) + d);
        double maximumFlow = Par("qmax"), maximumPressure = Par("pmax"), speed = u / 100;
        // Qpump² = Qmax²(n²−P/Pmax), Qdemand² = Qout²・P/100。
        double ratio = maximumFlow > 0 ? demand / maximumFlow : 0;
        double target = maximumFlow > 0 && maximumPressure > 0
            ? speed * speed / (1 / maximumPressure + ratio * ratio / 100)
            : 0;
        return Math.Max(0, FirstOrder(X, target, Par("tau"), dt));
    }
}
