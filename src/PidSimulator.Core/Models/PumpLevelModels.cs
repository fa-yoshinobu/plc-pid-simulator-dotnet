namespace PidSimulator.Core.Models;

/// <summary>回転数制御の遠心ポンプと一定断面積タンク。給水／排水で静揚程の向きが変わる。</summary>
public abstract class PumpLevelModelBase(ModelInfo info, int seed, bool filling) : ProcessModel(info, seed)
{
    private double _speed;

    protected override void ResetState() => _speed = 0;

    protected override double Integrate(double u, double d, double dt, double t)
    {
        _speed = FirstOrder(_speed, u / 100, Par("resp"), dt);
        double capacity = TankGeometry.CapacityCubicMetres(P);
        double scale = dt / 3600 / capacity * 100;
        double current = Math.Clamp(X, 0, 100);
        double shutoffHead = Par("hmax");
        double Head(double level) => Math.Max(0, Par("hstatic") + (filling ? 1 : -1) * level * Par("height") / 100000);
        double referenceMargin = 1 - Head(50) / shutoffHead;
        double PumpFlow(double level)
        {
            // 相似則 H∝回転数² と二次抵抗を合成。液位50%・MV100%でqmaxとなるよう校正。
            // 逆流と停止時のサイフォンは省略し、利用できる揚程がないときは流量0。
            if (_speed <= 0 || referenceMargin <= 0) return 0;
            double available = Math.Max(0, _speed * _speed - Head(level) / shutoffHead);
            return Par("qmax") * Math.Sqrt(available / referenceMargin);
        }

        double drainCoefficient = filling ? Par("kout") * Math.Max(0, InputOverride ?? 50) / 50 : 0;
        double inflow = filling ? 0 : Math.Max(0, InputOverride ?? Par("qin"));
        double NetFlow(double level) => filling
            ? PumpFlow(level) - drainCoefficient * Math.Sqrt(level) - d
            : inflow + d - PumpFlow(level);

        // x(new) = x(old) + Δt/V·(給水量(x(new))−排水量(x(new)))。
        // 正味流量は液位に対して単調減少なので解は一意。小容量・大流量でも平衡を飛び越さない。
        double Residual(double level) => level - current - scale * NetFlow(level);
        if (NetFlow(current) == 0) return current;
        if (Residual(0) >= 0) return 0;
        if (Residual(100) <= 0) return 100;
        double low = 0, high = 100;
        for (int i = 0; i < 48; i++)
        {
            double middle = (low + high) / 2;
            double residual = Residual(middle);
            if (residual == 0) return middle;
            if (residual < 0) low = middle;
            else high = middle;
        }
        return (low + high) / 2;
    }
}

/// <summary>給水ポンプの回転数で液位を制御。排出側は自然排水。</summary>
public sealed class LevelPumpInModel(ModelInfo info, int seed) : PumpLevelModelBase(info, seed, filling: true);

/// <summary>排水ポンプの回転数で液位を制御。流入側は一定流量。</summary>
public sealed class LevelPumpOutModel(ModelInfo info, int seed) : PumpLevelModelBase(info, seed, filling: false);
