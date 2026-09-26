using System.Globalization;

namespace PidSimulator.Core.Project;

/// <summary>試験データのCSV出力（仕様 §17）</summary>
public static class TrendCsv
{
    public static void Write(TextWriter w, ControlTarget target, IEnumerable<TrendSample> samples, Func<double, DateTime> toTime)
    {
        var info = target.Info;
        w.WriteLine($"# {target.Name}（{info.Name}） MV {target.MvAddress} / PV {target.PvAddress} / SP {(target.UseSp ? target.SpAddress : "内部")}");
        w.WriteLine($"時刻,SP[{target.SpRange.Unit}],PV[{target.PvRange.Unit}],MV[{target.MvRange.Unit}],外乱[{info.DistUnit}],FORCE,通信");
        foreach (var s in samples)
        {
            w.WriteLine(string.Join(",",
                toTime(s.T).ToString("yyyy/MM/dd HH:mm:ss.f", CultureInfo.InvariantCulture),
                F(s.Sp), F(s.Pv), F(s.Mv), F(s.Dist),
                s.Forced ? "ON" : "OFF",
                s.CommOk ? "正常" : "異常"));
        }
    }

    private static string F(double v) => double.IsFinite(v) ? v.ToString("0.####", CultureInfo.InvariantCulture) : "";
}
