namespace PidSimulator.Core.Models;

public sealed record ParamDef(string Key, string Label, string Unit, double Default, bool StopOnly = false, bool Warn = false, string Help = "")
{
    public IReadOnlyList<string> Choices => Key switch
    {
        "char" => ["リニア", "イコールパーセント", "クイックオープン"],
        "motorMode" => ["時定数・加減速", "慣性・トルク"],
        _ => [],
    };
    public double Min => Key switch
    {
        "cap" or "vol" or "area" or "height" or "nmax" or "acc" or "hmax" or "moment" or "torque" or "speedband" => 0.000001,
        "amb" or "tmin" or "ts" or "temp" or "tcool" => -273.14,
        _ => 0,
    };
    public double Max => Key switch
    {
        "char" => 2,
        "motorMode" => 1,
        "db" or "noise" or "pvar" or "speedband" => 100,
        "dead" => 600,
        "sens" or "tau" or "valve" or "resp" => 3600,
        "amb" or "tmin" or "ts" or "temp" or "tcool" => 1000,
        _ => 1e9,
    };
    public string Description => Help.Length > 0 ? Help : Key switch
    {
        "cap" => "水・容器などを合わせた熱容量。大きいほど温度が変わりにくくなります。",
        "tau" or "sens" or "valve" or "resp" => "一次遅れの時定数。約63%まで変化する時間です。約3倍で95%、0なら遅れなし。",
        "dead" => "MVが変化してからモデルへ伝わるまでの待ち時間。起動前のMVは0%として扱います。",
        "noise" => "標準PV幅に対するノイズの片側最大振幅。例：0.1なら±0.1%FS。",
        "hloss" => "周囲との温度差1℃あたりの熱移動量。周囲より低温なら熱が入ります。",
        "char" => "開度と流量の関係。イコールパーセントは開き始めが緩やかです。",
        "amb" => "熱のやり取りをする周囲の温度。",
        "res" => "基準状態に追加する配管抵抗の比率。全開実流量を直接指定する場合は0。",
        "pvar" => "基準差圧に対する周期的な圧力変動の振幅（周期約9秒）。",
        "acc" => "回転数の変化速度の上限。時定数による遅れも別に加わります。",
        "diameter" => "円筒タンクの内径。0なら断面積の入力値を使い、0より大きい場合は内径から断面積を計算します。縦置き・一定断面積のタンクを想定します。",
        "area" => "一定断面積のタンクの水平断面積。タンク内径が0のときだけ使用します。内径を指定した場合は、内径から計算した面積を使います。",
        "kout" => "排出弁50%での排水量 = この係数 × √液位[%]。条件から計算で実流量から換算できます。",
        "leak" => "ゲージ圧100 kPaでの漏れ量。圧力に比例する簡易近似で、実際の穴の流れとは異なります。",
        "qout" => "一定の空気消費量。0℃・101.325 kPaの標準体積で指定します。",
        "qsup" => "MV100%の供給流量。0℃・101.325 kPa基準。圧力による能力低下は省略した理想流量源です。",
        "temp" => "一定とするタンク内の空気温度。標準体積流量から圧力変化へ換算する際に使います。",
        "tmin" => "装置の冷却下限。冷却ではこれより下げません。初期温度が低い場合も瞬時に引き上げません。",
        "db" => "この開度未満では蒸気が流れない開き始めの不感帯。機械的な往復ヒステリシスは含みません。",
        _ => "",
    };
}

/// <summary>モデルの表示情報・標準値。PID値はダミーPLC（PLC側PIDの模擬）用。</summary>
public sealed record ModelInfo(
    ModelKind Kind, string Name, string Isa, string MvLabel, string PvLabel, string Unit,
    double PvMin, double PvMax, int Decimals, double InitPv, double DefaultSp,
    string DistLabel, string DistUnit, double DistMax, double DistDefault,
    string InputLabel, string InputUnit, double InputDefault,
    double PidKp, double PidTi, bool PidReverse,
    IReadOnlyList<ParamDef> Params)
{
    public double Span => PvMax - PvMin;
    public string Category => Kind switch
    {
        ModelKind.Motor => "回転数",
        ModelKind.Flow or ModelKind.PumpFlow => "流量",
        ModelKind.Level or ModelKind.LevelPumpIn or ModelKind.LevelPumpOut or ModelKind.LevelDrainValve => "液面",
        ModelKind.Heater or ModelKind.Steam or ModelKind.Chiller or ModelKind.CoolingWater => "温度",
        _ => "圧力",
    };
    public string ResponseDirection => PidReverse ? "MVを上げるとPVが下がる" : "MVを上げるとPVが上がる";
}

public static partial class ModelCatalog
{
    // 弁特性: 0=リニア 1=イコールパーセント 2=クイックオープン
    private static readonly Dictionary<ModelKind, ModelInfo> _all = new()
    {
        [ModelKind.Motor] = new(ModelKind.Motor, "モーター回転数", "SIC", "インバータ出力", "回転数", "rpm",
            0, 1800, 0, 0, 1200, "負荷増加", "%", 60, 20, "負荷", "%", 20, 0.8, 1.2, false,
        [
            new("motorMode", "モーターの計算方式", "", 0, StopOnly: true, Help: "時定数・加減速は従来の簡易応答。慣性・トルクは回転体の慣性とトルク制限付きの速度指令から回転数を計算します。"),
            new("nmax", "最大回転数", "rpm", 1800, StopOnly: true),
            new("tau", "時定数（簡易応答）", "s", 1.5, Warn: true, Help: "時定数・加減速方式でのみ使用。約63%まで変化する時間です。"),
            new("acc", "最大加減速（簡易応答）", "rpm/s", 900, Help: "時定数・加減速方式でのみ使用する回転数の変化速度上限。"),
            new("moment", "合計慣性モーメント J", "kg·m²", 0.1, StopOnly: true, Help: "慣性・トルク方式で使用。モーター軸に換算したモーターと負荷の慣性の合計。大きいほど同じトルクでの加減速が遅くなります。"),
            new("torque", "最大駆動・制動トルク", "N·m", 5, StopOnly: true, Help: "慣性・トルク方式で使用する正転駆動・制動トルクの上限。同じ上限の制動が可能な速度制御ドライブを近似します。"),
            new("damping", "粘性抵抗係数", "N·m·s/rad", 0.002, Help: "慣性・トルク方式で使用。回転数に比例する抵抗トルク = この係数 × 角速度。0なら粘性抵抗なし。"),
            new("speedband", "最大トルクに達する速度偏差", "%", 5, Help: "慣性・トルク方式で使用。指令との速度差が最大回転数のこの割合に達すると最大トルクになります。負荷があると定常速度偏差が残る比例速度制御の近似です。"),
            new("load", "負荷", "%", 20, Warn: true, Help: "簡易応答では負荷100%で定常回転数が40%低下。慣性・トルク方式では最大トルクに対する一定の抵抗トルクの割合。外乱の負荷増加も同じ単位です。"),
            new("dead", "むだ時間", "s", 0.2, StopOnly: true),
            new("sens", "センサ応答", "s", 0.1),
            new("noise", "ノイズ", "%FS", 0.3),
        ]),
        [ModelKind.Flow] = new(ModelKind.Flow, "流量（調節弁）", "FIC", "弁開度", "流量", "m³/h",
            0, 50, 1, 0, 30, "差圧低下", "%", 40, 15, "弁前後の差圧比", "%", 100, 0.6, 0.9, false,
        [
            new("qmax", "基準差圧・全開時の流量", "m³/h", 50, StopOnly: true, Help: "差圧比100%、追加配管抵抗0での全開流量。流量は差圧の平方根に比例します。"),
            new("char", "弁特性", "", 0, StopOnly: true),
            new("res", "配管抵抗", "%", 20, Warn: true),
            new("tau", "応答遅れ", "s", 1),
            new("dead", "むだ時間", "s", 0.5, StopOnly: true),
            new("pvar", "圧力変動", "%", 2),
            new("sens", "センサ応答", "s", 0.2),
            new("noise", "ノイズ", "%FS", 0.8),
        ]),
        [ModelKind.PumpFlow] = new(ModelKind.PumpFlow, "流量（インバーターポンプ）", "FIC", "ポンプ回転数指令", "流量", "m³/h",
            0, 50, 1, 0, 30, "揚程能力低下", "%", 40, 15, "ポンプ揚程能力比", "%", 100, 0.6, 0.9, false,
        [
            new("qmax", "100%回転数時の基準流量", "m³/h", 50, StopOnly: true, Help: "設定した静揚程で、揚程能力比100%・追加配管抵抗0のときの流量。定格回転数で実際に流れる量を指定します。"),
            new("hmax", "締切揚程（100%回転数）", "m", 30, StopOnly: true, Help: "定格回転数で流量0のときの揚程。通常の運転点の揚程ではありません。メーカーのポンプ曲線で確認できます。静揚程0なら流量への影響はありません。"),
            new("hstatic", "高低差・背圧（静揚程）", "m", 0, StopOnly: true, Help: "吸込側と吐出側の水面の高低差と圧力差を水柱高さへ換算した合計。循環回路は通常0。締切揚程より小さくしてください。負の静揚程・自然流下・逆流は扱いません。"),
            new("res", "追加配管抵抗", "%", 0, Warn: true, Help: "基準状態より増えた抵抗の簡易補正。通常は0。100なら流量は約71%になります。ポンプ曲線と配管抵抗を合成した二次係数への比率です。"),
            new("tau", "応答遅れ", "s", 1),
            new("dead", "むだ時間", "s", 0.5, StopOnly: true),
            new("pvar", "揚程能力変動", "%", 2, Help: "ポンプの基準揚程に対する周期的な能力変動の振幅（周期約9秒）。回転数の揺れを直接指定する値ではありません。"),
            new("sens", "センサ応答", "s", 0.2),
            new("noise", "ノイズ", "%FS", 0.8),
        ]),
        [ModelKind.Level] = new(ModelKind.Level, "液面（給水弁）", "LIC", "給水弁開度", "液面", "%",
            0, 100, 1, 50, 60, "排出流量増加", "m³/h", 15, 5, "排出弁開度", "%", 50, 3, 30, false,
        [
            new("height", "タンク高さ", "mm", 2000, StopOnly: true),
            new("diameter", "タンク内径（0は断面積を使用）", "mm", 0, StopOnly: true),
            new("area", "断面積", "m²", 1.8, StopOnly: true),
            new("qin", "最大流入量", "m³/h", 30, Warn: true),
            new("kout", "排出係数（自然排水）", "m³/h/√%", 2, Warn: true),
            new("valve", "バルブ応答", "s", 2),
            new("dead", "むだ時間", "s", 1, StopOnly: true),
            new("sens", "センサ遅れ", "s", 0.5),
            new("noise", "ノイズ", "%FS", 0.2),
        ]),
        [ModelKind.Heater] = new(ModelKind.Heater, "電気ヒーター", "TIC", "ヒーター出力", "温度", "℃",
            0, 200, 1, 25, 80, "熱負荷投入", "kW", 5, 2, "周囲温度", "℃", 25, 3, 40, false,
        [
            new("cap", "熱容量", "kJ/℃", 2, StopOnly: true),
            new("pmax", "最大加熱能力", "kW", 10, StopOnly: true),
            new("amb", "周囲温度", "℃", 25, Warn: true),
            new("hloss", "放熱係数", "kW/℃", 0.06),
            new("dead", "むだ時間", "s", 5, StopOnly: true),
            new("sens", "センサ応答", "s", 3),
            new("noise", "ノイズ", "%FS", 0.1),
        ]),
        [ModelKind.Steam] = new(ModelKind.Steam, "蒸気加熱", "TIC", "蒸気弁開度", "温度", "℃",
            0, 200, 1, 25, 90, "熱負荷投入", "kW", 15, 6, "蒸気供給能力", "%", 100, 3, 45, false,
        [
            new("cap", "熱容量", "kJ/℃", 6, StopOnly: true),
            new("pmax", "蒸気供給の加熱能力上限", "kW", 40, StopOnly: true, Help: "蒸気流量による供給能力の上限。実際の加熱量は伝熱量との小さい方になります。"),
            new("ts", "蒸気温度", "℃", 120, StopOnly: true, Help: "ジャケット・コイル内で凝縮する蒸気の温度。水との温度差が小さいほど加熱が弱くなります。"),
            new("ua", "熱の伝わりやすさ（UA）", "kW/℃", 1, StopOnly: true, Help: "全開時の伝熱係数×伝熱面積。条件から計算で自動設定できます。蒸気と水の温度差1℃あたりの加熱量です。"),
            new("char", "弁特性", "", 1, StopOnly: true),
            new("db", "デッドバンド", "%", 3, StopOnly: true),
            new("dead", "むだ時間", "s", 8, StopOnly: true),
            new("hloss", "放熱係数", "kW/℃", 0.2),
            new("amb", "周囲温度", "℃", 25),
            new("load", "熱負荷", "kW", 0, Warn: true),
            new("sens", "センサ応答", "s", 4),
            new("noise", "ノイズ", "%FS", 0.1),
        ]),
        [ModelKind.Chiller] = new(ModelKind.Chiller, "チラー", "TIC", "冷却能力指令", "温度", "℃",
            0, 50, 1, 25, 12, "発熱量増加", "kW", 6, 2, "熱負荷", "kW", 4, 0.8, 40, true,
        [
            new("cmax", "最大冷却能力", "kW", 12, StopOnly: true),
            new("cap", "熱容量", "kJ/℃", 3, StopOnly: true),
            new("amb", "周囲温度", "℃", 25),
            new("load", "熱負荷", "kW", 4, Warn: true),
            new("hloss", "放熱係数", "kW/℃", 0.1),
            new("resp", "冷却応答", "s", 10),
            new("dead", "むだ時間", "s", 6, StopOnly: true),
            new("tmin", "最低到達温度", "℃", 3, StopOnly: true),
            new("sens", "センサ応答", "s", 2),
            new("noise", "ノイズ", "%FS", 0.05),
        ]),
        [ModelKind.Pressure] = new(ModelKind.Pressure, "圧力（コンプレッサ）", "PIC", "コンプレッサ出力", "ゲージ圧力", "kPa(g)",
            0, 500, 0, 0, 300, "消費流量増加", "Nm³/h", 40, 15, "消費流量", "Nm³/h", 20, 1.5, 6, false,
        [
            new("vol", "容積", "m³", 2, StopOnly: true),
            new("temp", "タンク内の空気温度", "℃", 20, StopOnly: true),
            new("qsup", "最大供給量", "Nm³/h", 150, StopOnly: true),
            new("qout", "排出量", "Nm³/h", 20, Warn: true),
            new("leak", "100 kPaでの漏れ量", "Nm³/h", 20),
            new("resp", "応答", "s", 0.5),
            new("dead", "むだ時間", "s", 0.3, StopOnly: true),
            new("sens", "センサ応答", "s", 0.1),
            new("noise", "ノイズ", "%FS", 0.3),
        ]),
    };

    public static IReadOnlyCollection<ModelInfo> All => _all.Values;

    public static ModelInfo Get(ModelKind kind) => _all[kind];

    public static bool ValidateParameter(ModelKind kind, string key, double value, out string? error)
    {
        var def = Get(kind).Params.FirstOrDefault(p => p.Key == key);
        if (def == null) { error = $"パラメータ {key} はありません。"; return false; }
        if (!double.IsFinite(value) || value < def.Min || value > def.Max || (def.Choices.Count > 0 && value != Math.Truncate(value)))
        {
            error = $"「{def.Label}」は {def.Min:G} ～ {def.Max:G} {def.Unit} の{(def.Choices.Count > 0 ? "整数" : "数値")}で入力してください。";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>既定値を補った組合せを検証する。モデルにないキーは受け付けない。</summary>
    public static bool ValidateParameters(ModelKind kind, IReadOnlyDictionary<string, double> values, out string? error)
    {
        var parameters = Get(kind).Params.ToDictionary(p => p.Key, p => p.Default);
        foreach (var (key, value) in values)
        {
            if (!ValidateParameter(kind, key, value, out error)) return false;
            parameters[key] = value;
        }
        if (parameters.TryGetValue("diameter", out double diameter) && diameter > 0 && diameter < 0.001)
        {
            error = "タンク内径は0（断面積を使用）、または0.001 mm以上にしてください。";
            return false;
        }
        if (kind == ModelKind.PumpFlow && parameters["hstatic"] >= parameters["hmax"])
        {
            error = "高低差・背圧（静揚程）は、締切揚程より小さくしてください。";
            return false;
        }
        return ValidateAdditionalParameters(kind, parameters, out error);
    }

    public static ProcessModel Create(ModelKind kind, int seed = 0)
    {
        var info = Get(kind);
        return kind switch
        {
            ModelKind.Motor => new MotorModel(info, seed),
            ModelKind.Flow => new FlowModel(info, seed),
            ModelKind.PumpFlow => new PumpFlowModel(info, seed),
            ModelKind.Level => new LevelModel(info, seed),
            ModelKind.Heater => new HeaterModel(info, seed),
            ModelKind.Steam => new SteamModel(info, seed),
            ModelKind.Chiller => new ChillerModel(info, seed),
            ModelKind.Pressure => new PressureModel(info, seed),
            ModelKind.LevelPumpIn => new LevelPumpInModel(info, seed),
            ModelKind.LevelPumpOut => new LevelPumpOutModel(info, seed),
            ModelKind.LevelDrainValve => new LevelDrainValveModel(info, seed),
            ModelKind.CoolingWater => new CoolingWaterModel(info, seed),
            ModelKind.PressureSupplyValve => new PressureSupplyValveModel(info, seed),
            ModelKind.PressureExhaustValve => new PressureExhaustValveModel(info, seed),
            ModelKind.PumpPressure => new PumpPressureModel(info, seed),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }
}
