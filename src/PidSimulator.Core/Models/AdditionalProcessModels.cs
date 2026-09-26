namespace PidSimulator.Core.Models;

/// <summary>一定流入と、操作する自然排水弁による液面制御。</summary>
public sealed class LevelDrainValveModel(ModelInfo info, int seed) : ProcessModel(info, seed)
{
    private double _valve;

    protected override void ResetState() => _valve = 0;

    protected override double Integrate(double u, double d, double dt, double t)
    {
        _valve = FirstOrder(_valve, u, Par("valve"), dt);
        double qin = Math.Max(0, (InputOverride ?? Par("qin")) + d);
        double kout = Par("kout") * Valve(_valve / 100, Par("char"));
        double capacity = TankGeometry.CapacityCubicMetres(P);
        double scale = dt / 3600 / capacity * 100;
        // 後退オイラーの水収支を y=√液位 として解く。排水は液位を負にしない。
        double available = Math.Max(0, X + scale * qin);
        double drain = scale * kout;
        double root = available == 0 ? 0 : 2 * available / (Math.Sqrt(drain * drain + 4 * available) + drain);
        return Math.Clamp(root * root, 0, 100);
    }
}

/// <summary>ジャケット・コイルに流す冷却水の流量で温度を操作する。</summary>
public sealed class CoolingWaterModel(ModelInfo info, int seed) : ProcessModel(info, seed)
{
    private double _valve;

    protected override void ResetState() => _valve = 0;

    protected override double Integrate(double u, double d, double dt, double t)
    {
        _valve = FirstOrder(_valve, u, Par("resp"), dt);
        double heatCapacityRate = Par("cflow") * Valve(_valve / 100, Par("char")) * 1000 / 3600 * 4.19;
        double k = 0;
        if (heatCapacityRate > 0)
        {
            double ntu = Par("ua") / heatCapacityRate;
            k = ntu < 1e-6
                ? Par("ua") * (1 - ntu / 2 + ntu * ntu / 6)
                : heatCapacityRate * (1 - Math.Exp(-ntu));
        }
        double coolant = InputOverride ?? Par("tcool");
        double heat = Par("load") + d - Par("hloss") * (X - Par("amb")) - k * (X - coolant);
        // 水温より高い冷却水なら入熱となる。装置名による人工的な下限は設けない。
        return LinearStep(X, heat / Par("cap"), (Par("hloss") + k) / Par("cap"), dt);
    }
}
