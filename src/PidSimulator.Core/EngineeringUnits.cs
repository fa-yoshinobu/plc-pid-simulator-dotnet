using PidSimulator.Core.Models;

namespace PidSimulator.Core;

/// <summary>画面・PLCで扱う工業値と、モデルの百分率入力・液位の換算。</summary>
public static class EngineeringUnits
{
    private static readonly IReadOnlyList<string> RelayMvUnits = Array.AsReadOnly(new[] { "%" });
    private static readonly IReadOnlyList<string> SignalMvUnits = Array.AsReadOnly(new[] { "%", "V", "mA" });
    private static readonly IReadOnlyList<string> SpeedMvUnits = Array.AsReadOnly(new[] { "%", "Hz", "rpm", "V", "mA" });
    private static readonly IReadOnlyList<string> PowerMvUnits = Array.AsReadOnly(new[] { "%", "kW", "V", "mA" });

    /// <summary>モデルが操作する機器に対応した指令単位。V・mAはアナログ指令信号。</summary>
    public static IReadOnlyList<string> GetMvUnits(ModelKind kind, bool onOff = false) => onOff ? RelayMvUnits : kind switch
    {
        ModelKind.Motor or ModelKind.PumpFlow or ModelKind.LevelPumpIn or ModelKind.LevelPumpOut
            or ModelKind.PumpPressure or ModelKind.Pressure => SpeedMvUnits,
        ModelKind.Heater or ModelKind.Chiller => PowerMvUnits,
        _ => SignalMvUnits,
    };

    public static bool IsSupportedMvUnit(ModelKind kind, string? unit, bool onOff = false) =>
        GetMvUnits(kind, onOff).Contains(unit);

    public static bool IsLevel(ModelKind kind) => kind is ModelKind.Level or ModelKind.LevelPumpIn
        or ModelKind.LevelPumpOut or ModelKind.LevelDrainValve;

    public static bool IsMillimetres(ModelKind kind, string unit) =>
        IsLevel(kind) && string.Equals(unit.Trim(), "mm", StringComparison.OrdinalIgnoreCase);

    public static double TankHeight(ModelKind kind, IReadOnlyDictionary<string, double> parameters) =>
        parameters.TryGetValue("height", out double height) ? height
        : ModelCatalog.Get(kind).Params.First(p => p.Key == "height").Default;

    public static double PvFromModel(ModelKind kind, string unit, IReadOnlyDictionary<string, double> parameters, double value) =>
        IsMillimetres(kind, unit) ? value * TankHeight(kind, parameters) / 100 : value;

    public static double PvToModel(ModelKind kind, string unit, IReadOnlyDictionary<string, double> parameters, double value) =>
        IsMillimetres(kind, unit) ? value / TankHeight(kind, parameters) * 100 : value;

    public static double MvToPercent(RangeDef range, double value) =>
        range.Unit.Trim() == "%" ? Math.Clamp(value, 0, 100) : range.Fraction(value) * 100;

    public static double MvFromPercent(RangeDef range, double percent) =>
        range.Unit.Trim() == "%" ? Math.Clamp(percent, range.EngMin, range.EngMax)
        : range.EngMin + Math.Clamp(percent, 0, 100) / 100 * (range.EngMax - range.EngMin);
}
