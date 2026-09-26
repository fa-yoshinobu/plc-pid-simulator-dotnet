using System.Text.Json.Serialization;

namespace PidSimulator.Core;

/// <summary>PLC RAW値と工業値のレンジ・スケーリング（仕様 §5.3）</summary>
public sealed class RangeDef
{
    public double RawMin { get; set; }
    public double RawMax { get; set; } = 4000;
    public double EngMin { get; set; }
    public double EngMax { get; set; } = 100;
    public string Unit { get; set; } = "%";

    [JsonIgnore]
    public bool IsValid => double.IsFinite(RawMin) && double.IsFinite(RawMax)
        && double.IsFinite(EngMin) && double.IsFinite(EngMax)
        && RawMax > RawMin && EngMax > EngMin
        && double.IsFinite(RawMax - RawMin) && double.IsFinite(EngMax - EngMin);

    public double ToEng(double raw) =>
        (raw - RawMin) / (RawMax - RawMin) * (EngMax - EngMin) + EngMin;

    /// <summary>工業値をRAWへ変換する。レンジ外は最小／最大に制限する。</summary>
    public double ToRaw(double eng)
    {
        double c = Math.Clamp(eng, EngMin, EngMax);
        return (c - EngMin) / (EngMax - EngMin) * (RawMax - RawMin) + RawMin;
    }

    public double Fraction(double eng) => Math.Clamp((eng - EngMin) / (EngMax - EngMin), 0, 1);

    public RangeDef Clone() => (RangeDef)MemberwiseClone();

    public override string ToString() => $"RAW {RawMin}–{RawMax} ⇔ {EngMin}–{EngMax} {Unit}";
}
