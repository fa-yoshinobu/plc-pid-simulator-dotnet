namespace PidSimulator.Core.Models;

public static partial class ModelCatalog
{
    static ModelCatalog()
    {
        foreach (var info in AdditionalModels()) _all.Add(info.Kind, info);
    }

    private static ParamDef[] Delays(double dead, double sensor, double noise) =>
    [new("dead", "むだ時間", "s", dead, StopOnly: true), new("sens", "センサ応答", "s", sensor), new("noise", "ノイズ", "%FS", noise)];

    private static IEnumerable<ModelInfo> AdditionalModels()
    {
        foreach (bool drain in new[] { false, true })
            yield return new(drain ? ModelKind.LevelPumpOut : ModelKind.LevelPumpIn,
                drain ? "液面（排水ポンプ）" : "液面（給水ポンプ）", "LIC", "ポンプ回転数指令", "液面", "%",
                0, 100, 1, 50, 60, drain ? "流入量増加" : "排出流量増加", "m³/h", 15, 5,
                drain ? "流入量" : "排出弁開度", drain ? "m³/h" : "%", drain ? 15 : 50, 3, 30, drain,
            [
                new("height", "タンク高さ", "mm", 2000, StopOnly: true),
                new("diameter", "タンク内径（0は断面積を使用）", "mm", 0, StopOnly: true),
                new("area", "断面積", "m²", 1.8, StopOnly: true),
                new("qmax", "液位50%・最高回転時の流量", "m³/h", 30, StopOnly: true, Help: "最高回転で液位50%のときの実流量。液位による揚程の変化で実際の流量も変わります。"),
                new("hmax", "最高回転時の締切揚程", "m", 30, StopOnly: true, Help: "流れが止まった状態で出せる揚程。通常運転中の揚程とは異なります。"),
                new("hstatic", drain ? "空タンク基準の吐出先高さ" : "空タンク基準の高低差・背圧", "m", 0, StopOnly: true,
                    Help: drain ? "タンク底から吐出先までの高さに背圧相当の高さを加えます。水面が上がるほど必要揚程は減ります。サイフォン流れは省略します。" : "空タンクまでの静揚程。液位が上がるほど必要揚程が増えます。"),
                drain ? new("qin", "通常の流入量", "m³/h", 15, Warn: true) : new("kout", "排出係数（自然排水）", "m³/h/√%", 2, Warn: true),
                new("resp", "ポンプ回転数の応答", "s", 2),
                ..Delays(1, 0.5, 0.2),
            ]);

        yield return new(ModelKind.LevelDrainValve, "液面（排水弁）", "LIC", "排水弁開度", "液面", "%",
            0, 100, 1, 50, 60, "流入量増加", "m³/h", 15, 5, "流入量", "m³/h", 30, 3, 30, true,
        [
            new("height", "タンク高さ", "mm", 2000, StopOnly: true),
            new("diameter", "タンク内径（0は断面積を使用）", "mm", 0, StopOnly: true),
            new("area", "断面積", "m²", 1.8, StopOnly: true),
            new("qin", "通常の流入量", "m³/h", 30, Warn: true),
            new("kout", "排水弁全開時の排出係数", "m³/h/√%", 6, Warn: true, Help: "弁全開時の排水量 = この係数 × √液位[%]。ウィザードでは液位50%時の実流量から計算します。"),
            new("char", "排水弁特性", "", 0, StopOnly: true),
            new("valve", "排水弁の応答", "s", 2),
            ..Delays(1, 0.5, 0.2),
        ]);

        yield return new(ModelKind.CoolingWater, "温度（冷却水弁）", "TIC", "冷却水弁開度", "温度", "℃",
            0, 100, 1, 25, 18, "発熱量増加", "kW", 6, 2, "冷却水入口温度", "℃", 5, 3, 60, true,
        [
            new("cap", "水・容器の合計熱容量", "kJ/℃", 130.7, StopOnly: true),
            new("cflow", "冷却水の全開流量", "m³/h", 1.2, StopOnly: true, Help: "コイル・ジャケットを流れる冷却水の最大流量。タンク内の水量とは別です。"),
            new("tcool", "冷却水入口温度", "℃", 5, Warn: true, Help: "冷却水がタンクより温かい場合は加熱側に働きます。"),
            new("ua", "熱の伝わりやすさ（UA）", "kW/℃", 0.5, StopOnly: true, Help: "伝熱係数×面積。冷却水の温度上昇も考慮し、冷却量が流量に見合う範囲に制限されます。"),
            new("load", "常時入る熱", "kW", 2, Warn: true),
            new("amb", "周囲温度", "℃", 25),
            new("hloss", "放熱係数", "kW/℃", 0.01),
            new("char", "冷却水弁特性", "", 0, StopOnly: true),
            new("resp", "冷却水弁の応答", "s", 1),
            ..Delays(3, 1, 0.05),
        ]);

        foreach (bool exhaust in new[] { false, true })
            yield return new(exhaust ? ModelKind.PressureExhaustValve : ModelKind.PressureSupplyValve,
                exhaust ? "圧力（排気弁）" : "圧力（供給弁）", "PIC", exhaust ? "排気弁開度" : "供給弁開度", "ゲージ圧力", "kPa(g)",
                0, 600, 0, 200, 300, exhaust ? "供給流量増加" : "消費流量増加", "Nm³/h", 40, 15,
                exhaust ? "空気供給量" : "供給元圧力", exhaust ? "Nm³/h" : "kPa(g)", exhaust ? 20 : 600, 1.5, 6, exhaust,
            [
                new("vol", "タンク容積", "m³", 2, StopOnly: true),
                new("temp", "タンク・供給空気の温度", "℃", 20, StopOnly: true),
                new("qmax", exhaust ? "100 kPaでの全開排気量" : "大気へ放出時の全開供給量", "Nm³/h", 150, StopOnly: true,
                    Help: exhaust ? "20℃の空気、タンク100 kPa(g)から大気へ放出する基準能力。圧力差によって実流量が変わります。Nは0℃・1気圧基準。" : "20℃の空気、設定した供給元圧力から大気へ放出する基準能力。タンク圧が上がると供給量が減ります。Nは0℃・1気圧基準。"),
                exhaust ? new("qsup", "通常の空気供給量", "Nm³/h", 20, Warn: true, Help: "排気弁の操作とは独立した一定の流入量。Nは0℃・101.325 kPaの標準体積です。") : new("psupply", "供給元圧力", "kPa(g)", 600, StopOnly: true),
                ..(exhaust ? Array.Empty<ParamDef>() : new[] { new ParamDef("qout", "通常の空気消費量", "Nm³/h", 20, Warn: true) }),
                new("leak", "100 kPaでの漏れ量", "Nm³/h", 0),
                new("char", "弁特性", "", 0, StopOnly: true),
                new("resp", "弁の応答", "s", 0.5),
                ..Delays(0.3, 0.1, 0.3),
            ]);

        yield return new(ModelKind.PumpPressure, "圧力（インバーターポンプ）", "PIC", "ポンプ回転数指令", "吐出ゲージ圧力", "kPa(g)",
            0, 600, 0, 0, 200, "取出し量増加（100 kPa時）", "m³/h", 40, 10, "取出し量（100 kPa時）", "m³/h", 20, 0.8, 2, false,
        [
            new("pmax", "最高回転時の締切圧力", "kPa(g)", 500, StopOnly: true, Help: "取出し流量0での吐出圧力。遠心ポンプの圧力は回転数の2乗で変わります。"),
            new("qmax", "最高回転・無圧時の流量", "m³/h", 50, StopOnly: true, Help: "吐出圧力0でのポンプ流量。通常運転時の実流量とは異なります。ウィザードで実流量と圧力から換算できます。"),
            new("qout", "100 kPaでの取出し流量", "m³/h", 20, Warn: true, Help: "配管・使用先で消費する水量の基準。実際の取出し流量は圧力の平方根に比例します。"),
            new("tau", "圧力の応答遅れ", "s", 1),
            ..Delays(0.2, 0.1, 0.2),
        ]);
    }

    private static bool ValidateAdditionalParameters(ModelKind kind, IReadOnlyDictionary<string, double> p, out string? error)
    {
        error = null;
        if (kind is ModelKind.LevelPumpIn or ModelKind.LevelPumpOut)
        {
            double neededHead = p["hstatic"] + (kind == ModelKind.LevelPumpIn ? p["height"] / 1000 : 0);
            if (p["hmax"] <= neededHead)
            { error = "締切揚程は高低差・背圧より大きくしてください。給水ポンプではタンク高さも加えます。"; return false; }
        }
        if (kind == ModelKind.PumpPressure && p["pmax"] <= 0)
        { error = "締切圧力は0より大きくしてください。"; return false; }
        if (kind == ModelKind.PressureSupplyValve && p["psupply"] <= 0)
        { error = "供給元圧力は0より大きくしてください。"; return false; }
        return true;
    }
}
