using System.Globalization;
using System.Text.RegularExpressions;
using PidSimulator.Core.Project;

namespace PidSimulator.Core.Plc;

public static class PlcBitAddress
{
    public static bool TryWordBit(string address, out string word, out int bit, PlcSettings? plc = null)
    {
        word = ""; bit = 0;
        if (plc?.Mode == PlcMode.ModbusTcp)
        {
            if (!ModbusAddressRules.TryResolve(address, "BIT", false, out var parsed) || parsed.BitIndex is not int index) return false;
            word = parsed.BaseAddress;
            bit = index;
            return true;
        }
        if (plc?.Mode == PlcMode.HostLink)
        {
            if (!HostLinkAddressRules.TryResolve(address, "BIT", plc.Profile, out var parsed) || parsed.BitIndex is not int index) return false;
            word = parsed.BaseAddress;
            bit = index;
            return true;
        }
        string families = plc?.Mode == PlcMode.Slmp ? "D|W|R|ZR|SD|SW" : "HR|IR|DM|EM|FM|ZF|TM|CM|VM|D|E|F|W|R|ZR|SD|SW";
        var m = Regex.Match((address ?? "").Trim().ToUpperInvariant(), $@"^((?:{families})[0-9][0-9A-F]*)\.([0-9]+|[A-F])$");
        if (!m.Success) return false;
        string s = m.Groups[2].Value;
        bool valid = int.TryParse(s, s.Length == 1 && s[0] is >= 'A' and <= 'F' ? NumberStyles.HexNumber : NumberStyles.None,
            CultureInfo.InvariantCulture, out bit);
        if (!valid || bit is < 0 or > 15) return false;
        word = m.Groups[1].Value;
        return true;
    }
    public static bool IsValid(string address, PlcSettings? plc = null)
    {
        if (plc?.Mode == PlcMode.ModbusTcp)
            return ModbusAddressRules.TryResolve(address, "BIT", false, out _);
        if (plc?.Mode == PlcMode.HostLink)
            return HostLinkAddressRules.TryResolve(address, "BIT", plc.Profile, out _);
        if (TryWordBit(address, out _, out _, plc)) return true;
        if (plc?.Mode != PlcMode.Slmp && ModbusAddressRules.TryResolve(address, "BIT", false, out _)) return true;
        string text = (address ?? "").Trim().ToUpperInvariant();
        if (plc?.Mode != PlcMode.Slmp && Regex.IsMatch(text, @"^(?:R|MR|LR|CR|VB)[0-9][0-9A-F]*$")) return true;
        return Regex.IsMatch(text, @"^(?:M|Y|X|B|L|F|V|S|SM|SB|TS|TC|CS|CC)[0-9][0-9A-F]*$");
    }

    public static int Read(IReadOnlyDictionary<string, double> registers, string address)
    {
        if (TryWordBit(address, out var word, out var bit)) return ((int)registers.GetValueOrDefault(word) >> bit) & 1;
        return registers.GetValueOrDefault(address) == 0 ? 0 : 1;
    }
    public static void Write(Dictionary<string, double> registers, string address, bool on)
    {
        if (TryWordBit(address, out var word, out var bit))
        {
            int current = (int)registers.GetValueOrDefault(word);
            registers[word] = on ? current | (1 << bit) : current & ~(1 << bit);
        }
        else registers[address] = on ? 1 : 0;
    }
}
