using System.Globalization;
using System.Text.RegularExpressions;

namespace PidSimulator.Core.Plc;

/// <summary>Modbusの領域と0起点のアドレス。レジスタ内ビットは読込だけに使用する。</summary>
public readonly record struct ModbusAddress(string Device, ushort Number, int? BitIndex)
{
    public string BaseAddress => Device + Number.ToString(CultureInfo.InvariantCulture);
    public bool IsBitDevice => Device is "C" or "DI";
    public bool IsReadOnly => Device is "DI" or "IR" || BitIndex.HasValue;
    public ulong StartBit => IsBitDevice ? Number : (ulong)Number * 16 + (uint)(BitIndex ?? 0);
    public int BitCount(string dataType) => dataType == "BIT" ? 1 : dataType is "INT32" or "FLOAT32" ? 32 : 16;
}

/// <summary>Coil / Discrete Input / Holding Register / Input Register のアドレスと読書きの検証。</summary>
public static partial class ModbusAddressRules
{
    [GeneratedRegex(@"^(C|DI|HR|IR)([0-9]+)(?:\.([0-9]+|[A-F]))?$")]
    private static partial Regex AddressPattern();

    public static bool TryParse(string address, out ModbusAddress parsed)
    {
        parsed = default;
        var match = AddressPattern().Match((address ?? "").Trim().ToUpperInvariant());
        if (!match.Success || !ushort.TryParse(match.Groups[2].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out ushort number)) return false;

        string device = match.Groups[1].Value;
        int? bitIndex = null;
        if (match.Groups[3].Success)
        {
            string suffix = match.Groups[3].Value;
            var style = suffix.Length == 1 && suffix[0] is >= 'A' and <= 'F' ? NumberStyles.HexNumber : NumberStyles.None;
            if (device is "C" or "DI" || !int.TryParse(suffix, style, CultureInfo.InvariantCulture, out int bit)
                || bit is < 0 or > 15) return false;
            bitIndex = bit;
        }
        parsed = new(device, number, bitIndex);
        return true;
    }

    public static bool TryResolve(string address, string dataType, bool forWrite, out ModbusAddress parsed)
    {
        if (!TryParse(address, out parsed) || forWrite && parsed.IsReadOnly) return false;
        return dataType switch
        {
            "BIT" => parsed.IsBitDevice || parsed.BitIndex.HasValue,
            "INT16" or "UINT16" => !parsed.IsBitDevice && !parsed.BitIndex.HasValue,
            "INT32" or "FLOAT32" => !parsed.IsBitDevice && !parsed.BitIndex.HasValue && parsed.Number < ushort.MaxValue,
            _ => false,
        };
    }
}
