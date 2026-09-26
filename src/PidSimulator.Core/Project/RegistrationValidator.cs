using System.Globalization;
using System.Text.RegularExpressions;
using PidSimulator.Core.Plc;

namespace PidSimulator.Core.Project;

public enum CheckLevel { Ok, Warn, Block }

public sealed record CheckResult(CheckLevel Level, string Message);

/// <summary>登録時の設定チェック（仕様 §5.3：レンジとアドレス使用範囲の検証）</summary>
public static partial class RegistrationValidator
{
    [GeneratedRegex(@"^[A-Za-z]{1,3}[0-9][0-9A-Fa-f]*(\.(?:[0-9]|1[0-5]|[A-Fa-f]))?$")]
    private static partial Regex AddressPattern();

    public static bool IsAddress(string s) => AddressPattern().IsMatch(s ?? "");

    private static bool Same(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    // SLMPのワードデバイス。D/SD/Rは10進、W/SW/ZRは16進のデバイス番号。
    // デバイス・機種固有の拡張形式は推測せず、使用範囲を確認できない旨を表示する。
    [GeneratedRegex(@"^(SD|SW|ZR|D|W|R)([0-9][0-9A-F]*)$")]
    private static partial Regex WordAddressPattern();

    private readonly record struct AddressSpan(string Device, ulong StartBit, ulong EndBit);

    private static bool TryAddressSpan(string address, string dataType, out AddressSpan span)
    {
        span = default;
        string text = address.Trim().ToUpperInvariant();
        bool wordBit = PlcBitAddress.TryWordBit(text, out var word, out int bit);
        if (wordBit) text = word;
        if (dataType == "BIT" && !wordBit) return false;
        int words = dataType switch { "INT16" or "UINT16" => 1, "INT32" or "FLOAT32" => 2, "BIT" => 1, _ => 0 };
        if (words == 0) return false;
        var match = WordAddressPattern().Match(text);
        if (!match.Success) return false;
        string device = match.Groups[1].Value;
        var style = device is "W" or "SW" or "ZR" ? NumberStyles.HexNumber : NumberStyles.None;
        if (!uint.TryParse(match.Groups[2].Value, style, CultureInfo.InvariantCulture, out uint number)) return false;
        ulong start = (ulong)number * 16 + (wordBit ? (uint)bit : 0);
        span = new(device, start, start + (wordBit ? 1UL : (ulong)words * 16));
        return true;
    }

    private static bool Overlaps(string first, string firstType, string second, string secondType)
    {
        if (Same(first, second)) return true;
        // 既存のワード内ビットと同一ワードの検査は、拡張形式を解釈できない場合も維持する。
        if (PlcBitAddress.TryWordBit(first, out var firstWord, out _) && Same(firstWord, second)) return true;
        if (PlcBitAddress.TryWordBit(second, out var secondWord, out _) && Same(first, secondWord)) return true;
        return TryAddressSpan(first, firstType, out var a) && TryAddressSpan(second, secondType, out var b)
            && a.Device == b.Device && a.StartBit < b.EndBit && b.StartBit < a.EndBit;
    }

    /// <param name="others">自分以外の登録済み対象（編集時は編集中の対象を除く）</param>
    public static IReadOnlyList<CheckResult> Check(TargetConfig c, IEnumerable<ControlTarget> others)
    {
        var list = new List<CheckResult>();
        var o = others.ToList();
        string mvType = c.MvOnOff ? "BIT" : c.DataType;

        if (string.IsNullOrWhiteSpace(c.Name)) list.Add(new(CheckLevel.Block, "制御名称が未入力です"));
        if (!EngineeringUnits.IsSupportedMvUnit(c.Kind, c.MvRange.Unit, c.MvOnOff))
            list.Add(new(CheckLevel.Block, $"このモデル・制御方式のMV単位は {string.Join("・", EngineeringUnits.GetMvUnits(c.Kind, c.MvOnOff))} から選んでください"));
        if (c.MvOnOff && !PlcBitAddress.IsValid(c.MvAddress))
            list.Add(new(CheckLevel.Block, "ON/OFFのMVはM100・Y10などのビットデバイス、またはD1.0～D1.15のワード内ビットを指定してください"));
        foreach (var (label, r) in new[] { ("MV", c.MvRange), ("PV", c.PvRange), ("SP", c.SpRange) })
        {
            if (!r.IsValid) list.Add(new(CheckLevel.Block, $"{label} レンジは有限値で最小 < 最大を指定してください"));
            else if (!(label == "MV" && c.MvOnOff) && !PlcDataTypes.IsValidRange(c.DataType, r))
                list.Add(new(CheckLevel.Block, $"{label} RAWレンジは{c.DataType}で表せる範囲を指定してください（整数型は整数のみ）"));
        }
        if (EngineeringUnits.IsLevel(c.Kind)
            && (c.PvRange.Unit is not ("%" or "mm") || c.SpRange.Unit != c.PvRange.Unit))
            list.Add(new(CheckLevel.Block, "液面のPV・SP単位は同じ%またはmmを指定してください"));
        if (string.IsNullOrWhiteSpace(c.MvAddress) || string.IsNullOrWhiteSpace(c.PvAddress))
            list.Add(new(CheckLevel.Block, "MVアドレスとPVアドレスは必須です"));
        else if (Overlaps(c.MvAddress, mvType, c.PvAddress, c.DataType))
            list.Add(new(CheckLevel.Block, Same(c.MvAddress, c.PvAddress)
                ? $"MVとPVが同じアドレス（{c.PvAddress}）です"
                : $"MV {c.MvAddress} とPV {c.PvAddress} の使用範囲が重複しています。FLOAT32・INT32は2ワードを使用します。"));
        if (c.UseSp && Overlaps(c.SpAddress, c.DataType, c.PvAddress, c.DataType))
            list.Add(new(CheckLevel.Block, Same(c.SpAddress, c.PvAddress)
                ? $"SPとPVが同じアドレス（{c.PvAddress}）です"
                : $"SP {c.SpAddress} とPV {c.PvAddress} の使用範囲が重複しています。FLOAT32・INT32は2ワードを使用します。"));

        foreach (var (label, addr, type) in new[] { ("MV", c.MvAddress, mvType), ("PV", c.PvAddress, c.DataType), ("SP", c.UseSp ? c.SpAddress : "", c.DataType) })
        {
            if (!string.IsNullOrWhiteSpace(addr) && !IsAddress(addr))
                list.Add(new(CheckLevel.Warn, $"{label}アドレス「{addr}」の形式を確認してください（例: D1000）"));
            else if (!string.IsNullOrWhiteSpace(addr) && !TryAddressSpan(addr, type, out _)
                && !(type == "BIT" && PlcBitAddress.IsValid(addr)))
                list.Add(new(CheckLevel.Warn, $"{label}アドレス「{addr}」の使用範囲を確認できません。機種・アドレス形式と、他の信号との重複を確認してください。"));
        }

        var dup = o.FirstOrDefault(x => Overlaps(x.PvAddress, x.DataType, c.PvAddress, c.DataType));
        if (dup != null)
            list.Add(new(CheckLevel.Warn, $"PV書込アドレス {c.PvAddress} は「{dup.Name}」と重複しています。2つの対象が同じアドレスへ書き込みます。"));
        var reader = o.FirstOrDefault(x => Overlaps(x.MvAddress, x.MvDataType, c.PvAddress, c.DataType)
            || (x.UseSp && Overlaps(x.SpAddress, x.DataType, c.PvAddress, c.DataType)));
        if (reader != null)
            list.Add(new(CheckLevel.Warn, $"PV書込先 {c.PvAddress} は「{reader.Name}」の読込アドレスです。相手のMV/SPを上書きします。"));
        var writer = o.FirstOrDefault(x => Overlaps(x.PvAddress, x.DataType, c.MvAddress, mvType)
            || (c.UseSp && Overlaps(x.PvAddress, x.DataType, c.SpAddress, c.DataType)));
        if (writer != null)
            list.Add(new(CheckLevel.Warn, $"MV/SPの読込範囲は「{writer.Name}」のPV書込先 {writer.PvAddress} と重複しています。相手のPV書込みで読込値が上書きされます。"));
        if (o.Any(x => x.Name == c.Name.Trim()))
            list.Add(new(CheckLevel.Warn, "同じ名称の制御対象が既にあります"));

        if (dup == null && reader == null && !string.IsNullOrWhiteSpace(c.PvAddress))
            list.Add(new(CheckLevel.Ok, $"PV書込先 {c.PvAddress} は他の対象と重複していません"));
        if (list.All(x => x.Level != CheckLevel.Block))
            list.Add(new(CheckLevel.Ok, "レンジ・スケーリングの設定は有効です"));
        return list;
    }
}
