namespace PidSimulator.Core.Models;

public sealed record ConditionField(string Key, string Label, string Unit, double Default, double Min, double Max, string Hint = "", bool Advanced = false);
public sealed record ControlScenario(string Name, string Description, IReadOnlyList<ConditionField> Fields);
public sealed record CalculatedParameters(IReadOnlyDictionary<string, double> Values, string Explanation);

/// <summary>設備条件からプロセスモデルのパラメータを求める。PIDゲインは変更しない。</summary>
public static class ParameterCalculator
{
    public static IReadOnlyList<ControlScenario> Scenarios(ModelKind kind)
    {
        ConditionField F(string key, string label, string unit, double value, double min = 0, double max = 1e6, string hint = "", bool advanced = false) => new(key, label, unit, value, min, max, hint, advanced);
        ConditionField[] Response(double dead, double sensor) =>
        [F("dead", "操作から応答開始まで", "秒", dead, 0, 600, "むだ時間。分からなければ初期値を使います。", advanced: true), F("sens", "センサの応答時間", "秒", sensor, 0, 600, "変化の約63%に達する時間（時定数）。約3倍の時間で95%に達します。", advanced: true)];
        ControlScenario InertiaMotor(bool disc) => new(disc ? "円盤・フライホイールの慣性から設定" : "慣性モーメントとモーター能力から設定",
            "トルク制限のある速度制御ドライブを近似します。MVは回転数指令で、慣性が大きいほど加減速が遅くなります。",
            [F("rpm", "最高回転数", "rpm", 1800, 1, 100000),
             F("power", "最高回転数でのモーター出力", "kW", 0.75, 0.0001, 100000, "この出力と最高回転数から最大トルクを計算します。全回転域で同じトルク上限、制動時も同じトルク上限を使います。"),
             ..(disc ? new[] {
                 F("diskMass", "回転する円盤の重量", "kg", 20, 0.001, 1e6),
                 F("diskDiameter", "回転する円盤の直径", "mm", 400, 1, 100000, "均一な中実円盤が中心軸の周りを回る場合。減速機がある場合はモーター軸へ換算した慣性を使う方式を選んでください。"),
                 F("motorMoment", "モーター自身の慣性モーメント", "kg·m²", 0.01, 0.000001, 1e6, "円盤の慣性に加算します。", advanced: true)
             } : new[] { F("moment", "モーター・負荷の合計慣性モーメント", "kg·m²", 0.1, 0.000001, 1e6, "モーター軸に換算した合計値。機器の仕様書などから指定します。") }),
             F("load", "通常の負荷トルク", "%", 20, 0, 100, "最大トルクに対する一定の抵抗トルクの割合。", advanced: true),
             F("friction", "最高回転数での粘性抵抗による損失", "W", 0, 0, 1e6, "回転数に比例する抵抗トルク。分からなければ0から試します。通常の負荷トルクとは別です。", advanced: true),
             F("speedband", "最大トルクに達する速度偏差", "%", 5, 0.01, 100, "指令との速度差が最大回転数のこの割合に達すると最大トルクになります。負荷があると定常偏差が残る近似です。", advanced: true),
             ..Response(0.2, 0.1)]);
        if (kind is ModelKind.Heater or ModelKind.Steam or ModelKind.Chiller)
        {
            bool cool = kind == ModelKind.Chiller;
            string mode = kind switch { ModelKind.Steam => "ジャケット・コイルで水を加熱", ModelKind.Chiller => "循環水タンクを冷却", _ => "電気ヒーターで水を加熱" };
            ControlScenario Thermal(double litres, double minutes) => new($"{mode}（{litres:G} Lの例）",
                "撹拌された水とステンレス容器を想定。水量などは次の画面で変更できます。蒸気の直接吹込み・沸騰は対象外です。",
                [F("litres", "タンク内の水量", "L", litres, 0.01), F("mass", "ステンレス容器・コイルの重量", "kg", litres / 3, hint: "水と一緒に温度が変わる部分の合計。分からなければ初期値を使います。", advanced: true),
                 F("initial", "開始温度", "℃", 25, 0, 99), F("target", "目標温度", "℃", cool ? 12 : 80, 0, 99),
                 F("minutes", cool ? "全出力で冷却する時間" : "全出力で加熱する時間", "分", minutes, 0.1, 1440, "応答開始後の時間。PIDが出力を絞る時間は含みません。"),
                 ..(kind == ModelKind.Steam ? new[] { F("ts", "蒸気の温度", "℃", 120, 1, 300, "ジャケット・コイル内の蒸気温度。目標水温より高くします。") } : Array.Empty<ConditionField>()),
                 F("amb", "周囲温度", "℃", 25, -30, 60, advanced: true),
                 F("loss", "周囲との熱移動量（目標温度時）", "W", cool ? 100 : 500, 0, 1e6, "温度差による熱の出入り。断熱がよければ小さくします。熱の向きは温度差から自動計算します。", advanced: true),
                 ..(cool ? new[] {
                     F("load", "常時入る熱（機器・ポンプなど）", "W", 0, 0, 1e6, "周囲から入る熱とは別の発熱量。冷却能力に加算します。", advanced: true),
                     F("response", "冷却装置の応答時間", "秒", 1, 0, 600, "指令の約63%に達する時間（時定数）。約3倍で95%。指定した冷却時間とは別にかかります。", advanced: true),
                     F("minTemp", "冷却装置の最低到達温度", "℃", 3, 0, 99, "目標温度以下にします。水の凍結は再現しません。", advanced: true)
                 } : Array.Empty<ConditionField>()),
                 ..Response(kind == ModelKind.Steam ? 5 : 3, 3)]);
            return [Thermal(30, 10), Thermal(200, 30)];
        }
        if (kind == ModelKind.CoolingWater)
            return [new("冷却水を流して水タンクを冷却", "ジャケット・コイルを流れる冷却水と、水タンクの温度差で冷やします。流量が少ない場合は冷却能力にも上限があります。",
                [F("litres", "タンク内の水量", "L", 30, 0.01), F("initial", "開始温度", "℃", 60, 0, 99),
                 F("target", "目標温度", "℃", 25, 0, 99), F("minutes", "弁全開で冷却する時間", "分", 10, 0.1, 1440, "応答開始後の時間。弁・センサの応答とPIDで絞る時間は含みません。"),
                 F("tcool", "入口の冷却水温度", "℃", 5, 0, 99, "目標温度より低い水を供給します。"),
                 F("cflow", "弁全開時の冷却水流量", "L/分", 20, 0.01, 1e6, "計算できない場合は流量を増やすか、冷却時間を長くします。", advanced: true),
                 F("mass", "ステンレス容器・コイルの重量", "kg", 10, hint: "水と一緒に温度が変わる部分の合計。", advanced: true),
                 F("amb", "周囲温度", "℃", 25, -30, 60, advanced: true),
                 F("loss", "周囲との熱移動量（目標温度時）", "W", 0, 0, 1e6, "目標温度と周囲温度が同じなら0。熱の向きは温度差から自動計算します。", advanced: true),
                 F("load", "常時入る熱（機器・ポンプなど）", "W", 0, 0, 1e6, advanced: true),
                 F("response", "冷却水弁の応答時間", "秒", 1, 0, 600, "指令の約63%に達する時間。約3倍で95%。", advanced: true), ..Response(3, 1)])];
        IReadOnlyList<ControlScenario> scenarios = kind switch
        {
            ModelKind.Motor => [new("インバーター駆動のポンプ・ファン", "最高回転数と加速時間から応答を設定します。負荷なしの回転数応答を基準にします。",
                [F("rpm", "最高回転数", "rpm", 1800, 1, 100000), F("ramp", "加減速上限で0から最高回転数までの時間", "秒", 5, 0.1, 3600, "最大加減速の設定値。回転数の応答遅れも加わるため、実際の到達にはさらに時間がかかります。"), F("settle", "応答遅れだけで変化の95%に達する時間", "秒", 6, 0.1, 3600, "加減速制限がない場合の応答。実際には加減速上限と両方が働きます。", advanced: true), ..Response(0.2, 0.1)]), InertiaMotor(false), InertiaMotor(true)],
            ModelKind.Flow => [new("調節弁で水の流量を制御", "全開時の実流量を使用します。配管抵抗はその流量に含め、弁特性はリニアとします。",
                [F("flow", "弁全開時の流量", "L/分", 100, 0.01), F("settle", "流量が変化の95%に達する時間", "秒", 3, 0.1, 3600), ..Response(0.5, 0.2)])],
            ModelKind.PumpFlow => [new("インバーターポンプで水の流量を制御", "定格回転数で実際に流れる量を使います。高低差や背圧があると、低速では水が流れない場合も再現します。",
                [F("flow", "100%回転数での実流量", "L/分", 100, 0.01), F("settle", "流量が変化の95%に達する時間", "秒", 3, 0.1, 3600),
                 F("hmax", "締切揚程（100%回転数）", "m", 30, 0.01, 10000, "流量0のときのポンプ揚程。メーカーの曲線で確認します。", advanced: true),
                 F("hstatic", "高低差・背圧（静揚程）", "m", 0, 0, 10000, "循環配管なら通常0。締切揚程より小さくします。", advanced: true), ..Response(0.5, 0.2)])],
            ModelKind.Level => [new("給水弁と自然排水のタンク", "一定断面積のタンク。排水は液位の平方根に比例し、排出弁50%を基準にします。",
                [F("litres", "満水時のタンク容量", "L", 500, 0.01), F("height", "有効なタンク高さ", "mm", 1000, 1, 100000, "容量と高さから一定断面積を計算します。", advanced: true),
                 F("flow", "給水弁全開時の流量", "L/分", 50, 0.01), F("drain", "液位50%のときの排水量", "L/分", 20),
                 F("response", "給水弁の応答時間", "秒", 2, 0.1, 600, "指令の約63%に達する時間（時定数）。約3倍で95%に達します。", advanced: true), ..Response(1, 0.5)])],
            ModelKind.LevelPumpIn or ModelKind.LevelPumpOut => [new(kind == ModelKind.LevelPumpIn ? "給水ポンプでタンクの液面を制御" : "排水ポンプでタンクの液面を制御",
                "一定断面積の水タンク。100%回転数・液位50%での実流量を使います。液位による揚程の変化も含めます。",
                [F("litres", "満水時のタンク容量", "L", 500, 0.01), F("flow", "液位50%・100%回転数でのポンプ流量", "L/分", 50, 0.01),
                 F(kind == ModelKind.LevelPumpIn ? "drain" : "inlet", kind == ModelKind.LevelPumpIn ? "液位50%のときの自然排水量" : "通常の流入量", "L/分", 20, hint: kind == ModelKind.LevelPumpIn ? "排出弁50%を基準とします。" : "ポンプとは別に入ってくる水量。"),
                 F("height", "有効なタンク高さ", "mm", 1000, 1, 100000, advanced: true),
                 F("hmax", "締切揚程（100%回転数）", "m", 30, 0.01, 10000, "流量0のときのポンプ揚程。", advanced: true),
                 F("hstatic", "液位0%での高低差・背圧", "m", 0, 0, 10000, "給水では液位分の揚程が加わり、排水では液位分が給水圧になります。", advanced: true),
                 F("response", "ポンプの応答時間", "秒", 2, 0, 600, "回転数指令の約63%までの時間。約3倍で95%。", advanced: true), ..Response(1, 0.5)])],
            ModelKind.LevelDrainValve => [new("排水弁でタンクの液面を制御", "定常流入に対して、底部の自然排水弁を操作します。弁を開くほど液面が下がります。",
                [F("litres", "満水時のタンク容量", "L", 500, 0.01), F("inlet", "通常の流入量", "L/分", 20),
                 F("drain", "液位50%・排水弁全開での排水量", "L/分", 50, 0.01),
                 F("height", "有効なタンク高さ", "mm", 1000, 1, 100000, advanced: true),
                 F("response", "排水弁の応答時間", "秒", 2, 0, 600, "指令の約63%に達する時間。約3倍で95%。", advanced: true), ..Response(1, 0.5)])],
            ModelKind.Pressure => [new("エアタンクの圧力制御", "一定温度の空気タンク。流量は0℃・1気圧に換算した値を使用します。供給圧による流量低下は含みません。",
                [F("litres", "エアタンク容量", "L", 100, 0.01), F("supply", "最大空気供給量", "NL/分", 200, 0.01),
                 F("consume", "通常の空気消費量", "NL/分", 50), F("leak", "100 kPaでの漏れ量", "NL/分", 0, hint: "ゲージ圧100 kPaを基準に、圧力に比例する漏れを近似します。", advanced: true),
                 F("temp", "タンク内の空気温度", "℃", 20, -50, 150, "一定温度とみなします。圧縮時の一時的な温度上昇は再現しません。", advanced: true),
                 F("response", "供給装置の応答時間", "秒", 0.5, 0.1, 600, "指令の約63%に達する時間（時定数）。約3倍で95%に達します。", advanced: true), ..Response(0.3, 0.1)])],
            ModelKind.PressureSupplyValve => [new("供給弁でエアタンクの圧力を制御", "一定供給圧から弁を通して空気を入れます。タンク圧が供給圧に近づくほど流量が減ります。",
                [F("litres", "エアタンク容量", "L", 100, 0.01), F("psupply", "供給側のゲージ圧力", "kPa(g)", 600, 0.01, 100000),
                 F("flow", "供給圧から大気へ全開で流す流量", "NL/分", 200, 0.01, 1e6, "20℃の空気を基準とした弁の能力。運転中の実流量とは異なります。"),
                 F("consume", "通常の空気消費量", "NL/分", 50),
                 F("temp", "タンク内の空気温度", "℃", 20, -50, 150, advanced: true),
                 F("leak", "100 kPaでの漏れ量", "NL/分", 0, advanced: true),
                 F("response", "供給弁の応答時間", "秒", 0.5, 0, 600, "開度指令の約63%までの時間。約3倍で95%。", advanced: true), ..Response(0.3, 0.1)])],
            ModelKind.PressureExhaustValve => [new("排気弁でエアタンクの圧力を制御", "一定流入に対して、大気へ排気する弁を操作します。弁を開くほど圧力が下がります。",
                [F("litres", "エアタンク容量", "L", 100, 0.01), F("flow", "100 kPaから大気へ全開で流す流量", "NL/分", 500, 0.01, 1e6, "100 kPaはゲージ圧、空気温度20℃での弁の能力です。"),
                 F("supply", "通常の空気供給量", "NL/分", 200),
                 F("temp", "タンク内の空気温度", "℃", 20, -50, 150, advanced: true),
                 F("leak", "100 kPaでの漏れ量", "NL/分", 0, advanced: true),
                 F("response", "排気弁の応答時間", "秒", 0.5, 0, 600, "開度指令の約63%までの時間。約3倍で95%。", advanced: true), ..Response(0.3, 0.1)])],
            ModelKind.PumpPressure => [new("インバーターポンプで水の吐出圧力を制御", "定格回転数で同時に測った流量と吐出圧を使います。空気タンクとは異なり、ポンプと配管の流量の釣り合いから圧力を求めます。",
                [F("flow", "100%回転数での実流量", "L/分", 100, 0.01), F("pressure", "そのときの吐出ゲージ圧力", "kPa(g)", 300, 0.01, 100000),
                 F("settle", "圧力が変化の95%に達する時間", "秒", 3, 0.1, 3600),
                 F("pmax", "締切圧力（100%回転数）", "kPa(g)", 500, 0.01, 100000, "流量0のときのポンプ圧力。実流量測定時の吐出圧より高くします。", advanced: true), ..Response(0.3, 0.1)])],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (kind is ModelKind.Level or ModelKind.LevelPumpIn or ModelKind.LevelPumpOut or ModelKind.LevelDrainValve)
        {
            var capacityScenario = scenarios[0];
            return [capacityScenario with { Name = capacityScenario.Name + "（容量から）" },
                new(capacityScenario.Name + "（内径から）", capacityScenario.Description + " 縦置き円筒の内径と高さから容量を計算します。",
                    [F("diameter", "タンクの内径", "mm", 800, 1, 100000),
                     capacityScenario.Fields.Single(f => f.Key == "height") with { Advanced = false, Hint = "底から満水までの有効高さ。液面をmm表示する場合の満水値です。" },
                     ..capacityScenario.Fields.Where(f => f.Key is not ("litres" or "height"))])];
        }
        return scenarios;
    }

    public static CalculatedParameters Calculate(ModelKind kind, ControlScenario scenario, IReadOnlyDictionary<string, double> inputs)
    {
        foreach (var field in scenario.Fields)
            if (!inputs.TryGetValue(field.Key, out double v) || !double.IsFinite(v) || v < field.Min || v > field.Max)
                throw new ArgumentException($"{field.Label}は {field.Min:G} ～ {field.Max:G} {field.Unit} で入力してください。");
        double V(string key) => inputs[key];
        var p = new Dictionary<string, double> { ["dead"] = V("dead"), ["sens"] = V("sens") };
        string tankExplanation = "";
        if (kind is ModelKind.Level or ModelKind.LevelPumpIn or ModelKind.LevelPumpOut or ModelKind.LevelDrainValve)
        {
            p["height"] = V("height");
            p["diameter"] = inputs.GetValueOrDefault("diameter");
            p["area"] = p["diameter"] > 0 ? Math.PI * Math.Pow(p["diameter"] / 2000, 2) : V("litres") / V("height");
            tankExplanation = p["diameter"] > 0
                ? $"内径 {p["diameter"]:G5} mm、高さ {p["height"]:G5} mmから、断面積 = π × (内径 ÷ 2000)² = {p["area"]:G5} m²、満水容量 {p["area"] * p["height"]:G5} L。\n"
                : $"断面積 = タンク容量 ÷ 高さ = {p["area"]:G5} m²。内径は0とし、この断面積を使います。\n";
        }
        string explanation;
        switch (kind)
        {
            case ModelKind.Heater:
            case ModelKind.Steam:
            case ModelKind.Chiller:
                bool cool = kind == ModelKind.Chiller;
                double initial = V("initial"), target = V("target"), amb = V("amb");
                if (cool ? target >= initial : target <= initial)
                    throw new ArgumentException(cool ? "目標温度は開始温度より低くしてください。" : "目標温度は開始温度より高くしてください。");
                if (cool && target < V("minTemp"))
                    throw new ArgumentException("目標温度は冷却装置の最低到達温度以上にしてください。");
                double capacity = V("litres") * 4.19 + V("mass") * 0.5;
                double delta = Math.Abs(target - amb);
                if (delta < 1e-6 && V("loss") > 0) throw new ArgumentException("目標温度と周囲温度が同じ場合、周囲への熱移動は0 Wにしてください。");
                double loss = delta < 1e-6 ? 0 : V("loss") / 1000 / delta;
                double seconds = V("minutes") * 60;
                p["cap"] = capacity; p["amb"] = amb; p["hloss"] = loss;
                explanation = $"熱容量 = 水量 × 4.19 + ステンレス重量 × 0.5 = {capacity:G5} kJ/℃。\n";
                if (kind == ModelKind.Steam)
                {
                    double ts = V("ts");
                    if (ts <= target) throw new ArgumentException("蒸気の温度は目標温度より高くしてください。");
                    // Fully open heating is C dT/dt = UA(Ts-T) - H(T-Tamb).
                    // Heat transfer decreases as water warms, so the power limit set at
                    // the initial temperature does not bind during this heat-up.
                    double After(double ua)
                    {
                        double conductance = ua + loss;
                        if (conductance == 0) return initial;
                        double equilibrium = (ua * ts + loss * amb) / conductance;
                        return equilibrium + (initial - equilibrium) * Math.Exp(-conductance * seconds / capacity);
                    }
                    if (After(0) >= target) throw new ArgumentException("周囲との熱移動だけで目標に達します。時間・温度・熱移動量を見直してください。");
                    double lower = 0, upper = capacity / seconds;
                    while (After(upper) < target && upper < 1e12) upper *= 2;
                    if (After(upper) < target) throw new ArgumentException("指定した時間で加熱できる条件を計算できません。蒸気温度と加熱時間を見直してください。");
                    for (int i = 0; i < 80; i++)
                    {
                        double middle = (lower + upper) / 2;
                        if (After(middle) < target) lower = middle; else upper = middle;
                    }
                    p["ts"] = ts; p["ua"] = upper;
                    p["pmax"] = upper * (ts - initial);
                    p["load"] = 0; p["char"] = 1; p["db"] = 1;
                    explanation += $"{ts:G} ℃の蒸気で{initial:G} → {target:G} ℃を全開で{V("minutes"):G}分として、総合伝熱量 UA = {upper:G5} kW/℃を計算しました。\n" +
                        $"供給能力の上限は開始時の必要熱量 {p["pmax"]:G5} kWに設定します。水温が蒸気温度に近づくほど伝わる熱が減る近似です。\n";
                }
                else
                {
                    double exponent = loss * seconds / capacity;
                    double netPower = exponent < 1e-8 ? capacity * (target - initial) / seconds
                        : loss * ((target - amb) - (initial - amb) * Math.Exp(-exponent)) / (1 - Math.Exp(-exponent));
                    double heatLoad = cool ? V("load") / 1000 : 0;
                    double power = cool ? -netPower + heatLoad : netPower;
                    if (power <= 0) throw new ArgumentException("周囲との熱移動と常時熱負荷の条件だけで目標に達します。時間・温度・熱移動量を見直してください。");
                    p[cool ? "cmax" : "pmax"] = power;
                    if (cool) { p["load"] = heatLoad; p["resp"] = V("response"); p["tmin"] = V("minTemp"); }
                    explanation += $"{initial:G} → {target:G} ℃を全出力で{V("minutes"):G}分として、周囲との熱移動を含む熱収支から能力 {power:G5} kWを計算しました。\n";
                    if (cool) explanation += $"この冷却能力には常時入る熱 {heatLoad:G5} kWの除去分も含みます。\n";
                }
                explanation += "水は1 L ≒ 1 kg。一定熱容量・完全混合の近似です。沸騰・凍結は再現しません。むだ時間・センサ遅れ・冷却装置の応答分は別にかかります。初期PV・SPは変更しません。";
                break;
            case ModelKind.CoolingWater:
                double cwInitial = V("initial"), cwTarget = V("target"), cwTemperature = V("tcool");
                if (cwTarget >= cwInitial) throw new ArgumentException("目標温度は開始温度より低くしてください。");
                if (cwTarget <= cwTemperature) throw new ArgumentException("目標温度は入口の冷却水温度より高くしてください。");
                double cwCapacity = V("litres") * 4.19 + V("mass") * 0.5;
                double cwDelta = Math.Abs(cwTarget - V("amb"));
                if (cwDelta < 1e-6 && V("loss") > 0) throw new ArgumentException("目標温度と周囲温度が同じ場合、周囲との熱移動量は0 Wにしてください。");
                double cwLoss = cwDelta < 1e-6 ? 0 : V("loss") / 1000 / cwDelta;
                double cwLoad = V("load") / 1000, cwSeconds = V("minutes") * 60;
                double cwRate = V("cflow") / 60 * 4.19;
                double CooledAfter(double k)
                {
                    double total = cwLoss + k;
                    if (total == 0) return cwInitial + cwLoad * cwSeconds / cwCapacity;
                    double eq = (cwLoss * V("amb") + k * cwTemperature + cwLoad) / total;
                    return eq + (cwInitial - eq) * Math.Exp(-total * cwSeconds / cwCapacity);
                }
                if (CooledAfter(0) <= cwTarget) throw new ArgumentException("周囲との熱移動だけで目標温度以下になります。冷却時間・温度・熱移動量を見直してください。");
                if (CooledAfter(cwRate) >= cwTarget)
                    throw new ArgumentException("指定した冷却水流量では、この時間で目標温度まで冷やせません。詳細条件の冷却水流量を増やすか、冷却時間を長くする・冷却水温を下げてください。");
                double cwLower = 0, cwUpper = cwRate;
                for (int i = 0; i < 80; i++)
                {
                    double middle = (cwLower + cwUpper) / 2;
                    if (CooledAfter(middle) > cwTarget) cwLower = middle; else cwUpper = middle;
                }
                double cwUa = -cwRate * Math.Log(1 - cwUpper / cwRate);
                p["cap"] = cwCapacity; p["cflow"] = V("cflow") * 0.06; p["ua"] = cwUa;
                p["tcool"] = cwTemperature; p["amb"] = V("amb"); p["hloss"] = cwLoss; p["load"] = cwLoad;
                p["resp"] = V("response"); p["char"] = 0;
                explanation = $"熱容量 {cwCapacity:G5} kJ/℃、全開冷却水流量 {V("cflow"):G} L/分、入口温度 {cwTemperature:G} ℃から、{cwInitial:G} → {cwTarget:G} ℃を{V("minutes"):G}分で冷やすUA = {cwUa:G5} kW/℃を計算しました。\n" +
                    "冷却水の出口温度上昇を考慮し、有効な伝熱量は水の流量にも制限されます。水温が入口冷却水温度に近づくほど冷却は弱くなり、逆に冷却水が温かければ加熱されます。\n" +
                    "完全混合の水とステンレス容器を想定。沸騰・凍結は再現しません。弁応答・むだ時間・センサ遅れ・PIDが絞る時間は別にかかります。";
                break;
            case ModelKind.Motor:
                p["nmax"] = V("rpm");
                if (inputs.ContainsKey("power"))
                {
                    double maxSpeed = V("rpm") * Math.PI / 30;
                    p["motorMode"] = 1;
                    p["moment"] = inputs.ContainsKey("diskMass")
                        ? V("motorMoment") + V("diskMass") * Math.Pow(V("diskDiameter") / 2000, 2) / 2
                        : V("moment");
                    p["torque"] = V("power") * 1000 / maxSpeed;
                    p["damping"] = V("friction") / (maxSpeed * maxSpeed);
                    p["speedband"] = V("speedband"); p["load"] = V("load");
                    if (V("load") / 100 + V("friction") / (V("power") * 1000) >= 1)
                        throw new ArgumentException("通常の負荷と粘性抵抗がモーター能力以上です。モーター出力を増やすか、負荷・損失を下げてください。");
                    explanation = $"慣性・トルク方式を選択しました。合計慣性 J = {p["moment"]:G5} kg·m²、最大トルク = 出力 ÷ 角速度 = {p["torque"]:G5} N·m。\n" +
                        (inputs.ContainsKey("diskMass") ? "円盤の慣性 = 重量 × 半径² ÷ 2 にモーター自身の慣性を加算しました。均一な中実円盤を中心軸で回す場合の計算です。\n" : "") +
                        "慣性 × 角加速度 = 駆動トルク − 粘性抵抗 − 負荷トルク。MVは回転数指令で、速度差に比例する駆動・制動トルクを最大トルクに制限します。負荷があると小さな定常速度偏差が残ります。\n" +
                        "同じトルクで加速・制動できるドライブを近似します。逆転・電気回路・モーターの詳細なトルク曲線は含みません。時定数・最大加減速の値はこの方式では使用しません。";
                }
                else
                {
                    p["motorMode"] = 0; p["acc"] = V("rpm") / V("ramp"); p["tau"] = V("settle") / -Math.Log(0.05); p["load"] = 0;
                    explanation = "時定数・加減速方式を選択しました。最大加減速 = 最高回転数 ÷ 加減速上限での到達時間。時定数 = 応答遅れ単独の95%到達時間 ÷ 2.996。両方が同時に働くため、実際の到達には指定した加減速時間より長くかかります。";
                }
                break;
            case ModelKind.Flow:
                p["qmax"] = V("flow") * 0.06; p["tau"] = V("settle") / -Math.Log(0.05); p["char"] = 0; p["res"] = 0; p["pvar"] = 0;
                explanation = "最大流量 = L/分 × 0.06（m³/h）。時定数 = 95%到達時間 ÷ 2.996。入力流量を全開時の実流量として、配管抵抗・供給圧変動は0、弁特性はリニアに設定します。";
                break;
            case ModelKind.PumpFlow:
                p["qmax"] = V("flow") * 0.06; p["tau"] = V("settle") / -Math.Log(0.05);
                p["hmax"] = V("hmax"); p["hstatic"] = V("hstatic"); p["res"] = 0; p["pvar"] = 0;
                explanation = "100%回転数での実流量 = L/分 × 0.06（m³/h）。時定数 = 95%到達時間 ÷ 2.996。入力した静揚程で基準流量になるよう、ポンプと配管を合わせた二次特性を使います。\n静揚程0なら流量は回転数に比例する近似です。静揚程があると低速では流れません。追加配管抵抗・周期変動は0に設定します。";
                break;
            case ModelKind.Level:
                p["qin"] = V("flow") * 0.06;
                p["kout"] = V("drain") * 0.06 / Math.Sqrt(50); p["valve"] = V("response");
                explanation = tankExplanation + $"排出係数は液位50%の排水量から計算します。\n排水なし・給水弁全開なら満水まで約{p["area"] * p["height"] / V("flow"):G4}分（弁応答・むだ時間を除く）。";
                break;
            case ModelKind.LevelPumpIn:
            case ModelKind.LevelPumpOut:
                p["qmax"] = V("flow") * 0.06;
                p["hmax"] = V("hmax"); p["hstatic"] = V("hstatic"); p["resp"] = V("response");
                if (kind == ModelKind.LevelPumpIn) p["kout"] = V("drain") * 0.06 / Math.Sqrt(50);
                else p["qin"] = V("inlet") * 0.06;
                explanation = tankExplanation + "液位50%・100%回転数での実流量をポンプの基準にします。\n" +
                    (kind == ModelKind.LevelPumpIn
                        ? "給水ポンプは液位が高くなるほど揚程が増えて流れにくくなります。自然排水は液位の平方根に比例し、入力した排水量は液位50%・排出弁50%を基準とします。"
                        : "排水ポンプを速くするほど液面が下がります。高い液位からの圧力が排水を助けます。ポンプ停止時の自然流下と逆流は含めません。");
                break;
            case ModelKind.LevelDrainValve:
                p["qin"] = V("inlet") * 0.06;
                p["kout"] = V("drain") * 0.06 / Math.Sqrt(50); p["valve"] = V("response"); p["char"] = 0;
                explanation = tankExplanation + "全開排水係数 = 液位50%時の排水量 × 0.06 ÷ √50。\n排水は液位の平方根に比例し、弁特性はリニアに設定します。定常流入と操作する排水弁の差で液面が変化します。";
                break;
            case ModelKind.Pressure:
                if (V("supply") <= V("consume")) throw new ArgumentException("最大供給量は通常の消費量より大きくしてください。");
                p["vol"] = V("litres") / 1000; p["qsup"] = V("supply") * 0.06; p["qout"] = V("consume") * 0.06;
                p["leak"] = V("leak") * 0.06; p["resp"] = V("response"); p["temp"] = V("temp");
                explanation = $"容積 = L ÷ 1000（m³）、流量 = NL/分 × 0.06（Nm³/h）。Nは0℃・101.325 kPa基準です。\nタンク内は{V("temp"):G}℃一定とし、標準状態との絶対温度比 ({V("temp"):G} + 273.15) ÷ 273.15 を掛けて圧力変化を計算します。表示は大気圧を0としたゲージ圧です。圧縮熱や供給圧上限は再現しません。";
                break;
            case ModelKind.PressureSupplyValve:
            case ModelKind.PressureExhaustValve:
                p["vol"] = V("litres") / 1000; p["temp"] = V("temp"); p["qmax"] = V("flow") * 0.06;
                p["leak"] = V("leak") * 0.06; p["resp"] = V("response"); p["char"] = 0;
                if (kind == ModelKind.PressureSupplyValve)
                {
                    if (V("consume") >= V("flow")) throw new ArgumentException("弁全開時の基準流量は通常の消費量より大きくしてください。");
                    p["psupply"] = V("psupply"); p["qout"] = V("consume") * 0.06;
                }
                else p["qsup"] = V("supply") * 0.06;
                explanation = "容積 = L ÷ 1000（m³）、流量 = NL/分 × 0.06（Nm³/h）。Nは0℃・101.325 kPa基準で、弁の能力値は20℃の空気での値を指定します。\n" +
                    (kind == ModelKind.PressureSupplyValve
                        ? "全開の基準流量は、指定した供給圧から大気へ流したときの値です。タンク圧が供給圧に近づくほど流入量が減り、供給圧以上へは押し込みません。"
                        : "全開の基準流量は、100 kPa(g)から大気へ排気したときの値です。一定供給に対して排気弁を開くほど圧力が下がります。") +
                    $"\nタンクは{V("temp"):G}℃一定の理想気体、弁特性はリニアです。気体の圧力比による流量変化とチョークを近似し、圧縮熱・逆流は含めません。";
                break;
            case ModelKind.PumpPressure:
                if (V("pmax") <= V("pressure")) throw new ArgumentException("締切圧力は、実流量を測ったときの吐出圧力より高くしてください。");
                p["pmax"] = V("pmax"); p["qmax"] = V("flow") * 0.06 / Math.Sqrt(1 - V("pressure") / V("pmax"));
                p["qout"] = V("flow") * 0.06 / Math.Sqrt(V("pressure") / 100); p["tau"] = V("settle") / -Math.Log(0.05);
                explanation = $"100%回転数での {V("flow"):G} L/分・{V("pressure"):G} kPa(g)という運転点から、無圧時のポンプ流量 {p["qmax"]:G5} m³/hと100 kPaでの配管需要量 {p["qout"]:G5} m³/hを計算しました。\n" +
                    "ポンプ揚程は回転数の二乗に比例し、流量が増えると低下する近似です。配管の流量は圧力の平方根に比例します。圧力応答の時定数は95%到達時間 ÷ 2.996。水撃・配管の圧縮性・静水頭は含めません。";
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        foreach (var (key, value) in p)
            if (!ModelCatalog.ValidateParameter(kind, key, value, out var error))
                throw new ArgumentException($"計算結果がモデルの設定範囲を超えています。入力条件を見直してください。\n{error}");
        if (!ModelCatalog.ValidateParameters(kind, p, out var pairError))
            throw new ArgumentException(pairError);
        return new(p, explanation + "\nノイズなど一覧にないパラメータは現在値を保持します。レンジ・初期PV・SP・PLC側PIDは別途設定してください。");
    }
}
