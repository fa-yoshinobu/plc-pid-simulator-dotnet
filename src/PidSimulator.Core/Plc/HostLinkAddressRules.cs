using System.Globalization;
using System.Text.RegularExpressions;

namespace PidSimulator.Core.Plc;

/// <summary>アプリで扱う通常ワードとリレーのアドレス。機種ごとの使用可能な番号上限はPLCで確認する。</summary>
public readonly record struct HostLinkAddress(string Device, int Number, int? BitIndex, bool IsBitDevice)
{
    public string BaseAddress => Device + (Device switch
    {
        "B" or "VB" or "W" => Number.ToString("X", CultureInfo.InvariantCulture),
        "X" or "Y" => (Number / 16).ToString(CultureInfo.InvariantCulture) + (Number % 16).ToString("X", CultureInfo.InvariantCulture),
        _ => Number.ToString(CultureInfo.InvariantCulture),
    });

    public ulong StartBit => IsBitDevice
        ? Device is "R" or "MR" or "LR" or "CR" ? (ulong)(Number / 100) * 16 + (uint)(Number % 100) : (uint)Number
        : (ulong)Number * 16 + (uint)(BitIndex ?? 0);

    public int BitCount(string dataType) => dataType == "BIT" ? 1 : dataType is "INT32" or "FLOAT32" ? 32 : 16;
}

/// <summary>
/// Host Link の登録アドレスを検査する。数値信号は通常ワード、ON/OFF信号はリレーまたはワード内ビット。
/// タイマ・カウンタなど応答形式が異なるデバイスは対象外。
/// </summary>
public static partial class HostLinkAddressRules
{
    [GeneratedRegex(@"^(DM|EM|FM|ZF|TM|CM|VM|MR|LR|CR|VB|R|B|W|X|Y|M|L|D|E|F)([0-9A-F]+)(?:\.([0-9]+|[A-F]))?$")]
    private static partial Regex AddressPattern();

    public static bool TryParse(string address, string profile, out HostLinkAddress resolved)
    {
        resolved = default;
        bool xym = profile?.EndsWith("-xym", StringComparison.Ordinal) == true;
        string baseProfile = xym ? profile![..^4] : profile ?? "";
        if (baseProfile is not ("keyence:kv-nano" or "keyence:kv-3000" or "keyence:kv-5000"
            or "keyence:kv-7000" or "keyence:kv-8000" or "keyence:kv-x500")) return false;

        var match = AddressPattern().Match((address ?? "").Trim().ToUpperInvariant());
        if (!match.Success) return false;
        string device = match.Groups[1].Value;
        if (xym ? device is "R" or "MR" or "LR" or "DM" or "EM" or "FM"
                : device is "X" or "Y" or "M" or "L" or "D" or "E" or "F") return false;
        // これらは該当機種のデバイス表に存在しない。
        if (baseProfile == "keyence:kv-nano" && device is "EM" or "FM" or "ZF" or "E" or "F") return false;
        if (baseProfile == "keyence:kv-x500" && device is "VM" or "VB") return false;

        string digits = match.Groups[2].Value;
        int number;
        if (device is "X" or "Y")
        {
            if (!int.TryParse(digits.Length == 1 ? "0" : digits[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int bank)
                || !int.TryParse(digits[^1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int bit)
                || bank > (int.MaxValue - bit) / 16) return false;
            number = bank * 16 + bit;
        }
        else
        {
            var style = device is "B" or "VB" or "W" ? NumberStyles.HexNumber : NumberStyles.None;
            if (!int.TryParse(digits, style, CultureInfo.InvariantCulture, out number) || number < 0) return false;
        }
        if (device is "R" or "MR" or "LR" or "CR" && number % 100 > 15) return false;

        bool bitDevice = device is "R" or "MR" or "LR" or "CR" or "B" or "VB" or "X" or "Y" or "M" or "L";
        int? bitIndex = null;
        if (match.Groups[3].Success)
        {
            string suffix = match.Groups[3].Value;
            var style = suffix.Length == 1 && suffix[0] is >= 'A' and <= 'F' ? NumberStyles.HexNumber : NumberStyles.None;
            if (bitDevice || !int.TryParse(suffix, style, CultureInfo.InvariantCulture, out int bit) || bit is < 0 or > 15) return false;
            bitIndex = bit;
        }
        resolved = new HostLinkAddress(device, number, bitIndex, bitDevice);
        return true;
    }

    public static bool TryResolve(string address, string dataType, string profile, out HostLinkAddress resolved)
    {
        if (!TryParse(address, profile, out resolved)) return false;
        return dataType switch
        {
            "BIT" => resolved.IsBitDevice || resolved.BitIndex.HasValue,
            "INT16" or "UINT16" or "INT32" or "FLOAT32" => !resolved.IsBitDevice && !resolved.BitIndex.HasValue,
            _ => false,
        };
    }
}
