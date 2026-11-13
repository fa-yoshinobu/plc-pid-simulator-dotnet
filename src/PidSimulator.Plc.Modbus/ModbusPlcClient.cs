using System.Diagnostics;
using System.Net.Sockets;
using AMWD.Protocols.Modbus.Common;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;

namespace PidSimulator.Plc.Modbus;

/// <summary>Modbus TCP Client。MV/SPを読み、PVをHolding Registerへ書く。</summary>
public sealed class ModbusPlcClient : CachedPlcClient<ModbusSession>
{
    private readonly byte _unitId;

    public ModbusPlcClient(PlcSettings settings) : base(settings, EndpointText(settings.Effective()))
    {
        _unitId = checked((byte)Settings.ModbusUnitId);
    }

    private static string EndpointText(PlcSettings s) => $"Modbus TCP {s.Host}:{s.Port}（Unit ID {s.ModbusUnitId}）";

    protected override bool IsClientUsable(ModbusSession client) => client.IsUsable;

    protected override async Task<ModbusSession> OpenClientAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var session = new ModbusSession(Settings);
        try
        {
            await ProbeAsync(session, ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private Task<object> ProbeAsync(ModbusSession session, CancellationToken ct)
    {
        ModbusAddressRules.TryParse(Settings.ModbusTestAddress, out var address);
        return ReadValueAsync(session, Settings.ModbusTestAddress, address.IsBitDevice || address.BitIndex.HasValue ? "BIT" : "U", ct);
    }

    public static async Task<PlcTestResult> TestConnectionAsync(PlcSettings settings, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var plc = new ModbusPlcClient(settings);
            await using var session = new ModbusSession(plc.Settings);
            object value = await plc.ProbeAsync(session, ct).ConfigureAwait(false);
            double raw = Convert.ToDouble(value);
            return new PlcTestResult(true, raw,
                $"接続できました（応答 {sw.Elapsed.TotalMilliseconds:0} ms、{plc.Settings.ModbusTestAddress} = {raw:G9}）",
                sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PlcTestResult(false, 0, $"接続できません：{Describe(ex)}", sw.Elapsed.TotalMilliseconds);
        }
    }

    protected override Task<object> ReadValueAsync(ModbusSession session, string address, string suffix, CancellationToken ct)
    {
        var point = Resolve(address, suffix, forWrite: false);
        return session.ExecuteAsync<object>(async (client, token) =>
        {
            if (point.Device == "C")
            {
                var values = await client.ReadCoilsAsync(_unitId, point.Number, 1, token).ConfigureAwait(false);
                if (values.Count != 1) throw new InvalidDataException("Coil応答の点数が一致しません。");
                return values[0].Value;
            }
            if (point.Device == "DI")
            {
                var values = await client.ReadDiscreteInputsAsync(_unitId, point.Number, 1, token).ConfigureAwait(false);
                if (values.Count != 1) throw new InvalidDataException("Discrete Input応答の点数が一致しません。");
                return values[0].Value;
            }
            ushort count = suffix is "L" or "F" ? (ushort)2 : (ushort)1;
            ushort[] words = point.Device == "HR"
                ? (await client.ReadHoldingRegistersAsync(_unitId, point.Number, count, token).ConfigureAwait(false)).Select(v => v.Value).ToArray()
                : (await client.ReadInputRegistersAsync(_unitId, point.Number, count, token).ConfigureAwait(false)).Select(v => v.Value).ToArray();
            if (words.Length != count) throw new InvalidDataException("レジスタ応答の点数が一致しません。");
            if (point.BitIndex is { } bit) return (words[0] & (1 << bit)) != 0;
            if (count == 1) return suffix == "S" ? (object)unchecked((short)words[0]) : words[0];
            uint bits = Settings.ModbusWordOrder == ModbusWordOrder.HighWordFirst
                ? (uint)words[0] << 16 | words[1] : (uint)words[1] << 16 | words[0];
            return suffix == "F" ? (object)BitConverter.Int32BitsToSingle(unchecked((int)bits)) : unchecked((int)bits);
        }, write: false, ct);
    }

    protected override async Task<IReadOnlyDictionary<string, object>> ReadValuesAsync(ModbusSession session, string[] keys, CancellationToken ct)
    {
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in keys)
        {
            int colon = key.LastIndexOf(':');
            values[key] = await ReadValueAsync(session, key[..colon], key[(colon + 1)..], ct).ConfigureAwait(false);
        }
        return values;
    }

    protected override int WriteBatchSize => 1;

    protected override Task WriteValuesAsync(ModbusSession session, IReadOnlyDictionary<string, object> updates, CancellationToken ct)
    {
        var point = updates.Single();
        int colon = point.Key.LastIndexOf(':');
        return WriteValueAsync(session, point.Key[..colon], point.Key[(colon + 1)..], Convert.ToDouble(point.Value), ct);
    }

    protected override Task WriteValueAsync(ModbusSession session, string address, string suffix, double raw, CancellationToken ct)
    {
        var point = Resolve(address, suffix, forWrite: true);
        return session.ExecuteAsync(async (client, token) =>
        {
            bool written;
            if (point.Device == "C")
                written = await client.WriteSingleCoilAsync(_unitId, new Coil { Address = point.Number, Value = raw != 0 }, token).ConfigureAwait(false);
            else if (suffix is "F" or "L")
            {
                uint bits = suffix == "F" ? unchecked((uint)BitConverter.SingleToInt32Bits((float)raw)) : unchecked((uint)(int)raw);
                ushort high = (ushort)(bits >> 16), low = (ushort)bits;
                bool highFirst = Settings.ModbusWordOrder == ModbusWordOrder.HighWordFirst;
                // 32ビット値は隣接2レジスタをFC16で一度に送る。
                written = await client.WriteMultipleHoldingRegistersAsync(_unitId,
                [
                    new HoldingRegister { Address = point.Number, Value = highFirst ? high : low },
                    new HoldingRegister { Address = (ushort)(point.Number + 1), Value = highFirst ? low : high },
                ], token).ConfigureAwait(false);
            }
            else
            {
                ushort word = suffix == "S" ? unchecked((ushort)(short)raw) : (ushort)raw;
                // 16ビットのAO値は単一レジスタ書込（FC06）を使用する。
                written = await client.WriteSingleHoldingRegisterAsync(_unitId, new HoldingRegister { Address = point.Number, Value = word }, token).ConfigureAwait(false);
            }
            if (!written) throw new InvalidDataException("書込応答のアドレス・値が要求と一致しません。");
            return true;
        }, write: true, ct);
    }

    private static ModbusAddress Resolve(string address, string suffix, bool forWrite)
    {
        string type = suffix switch { "BIT" => "BIT", "U" => "UINT16", "L" => "INT32", "F" => "FLOAT32", _ => "INT16" };
        if (!ModbusAddressRules.TryResolve(address, type, forWrite, out var point))
            throw new ArgumentException($"{(forWrite ? "書込" : "読込")}に使える{type}のModbusアドレスを指定してください：{address}");
        return point;
    }

    protected override bool IsPointError(Exception error) =>
        error is ModbusException { ErrorCode: not ModbusErrorCode.NoError } or ArgumentException or FormatException;

    protected override string DescribeError(Exception error) => Describe(error);

    public static string Describe(Exception error) => error switch
    {
        ModbusWriteOutcomeUnknownException e => $"書込み結果が不明：{Describe(e.InnerException!)}",
        TimeoutException => "応答タイムアウト",
        ModbusException { ErrorCode: not ModbusErrorCode.NoError } e => $"Modbus例外応答 0x{(byte)e.ErrorCode:X2}（{e.ErrorMessage}）",
        ModbusException e => $"Modbus応答が不正：{e.Message}",
        SocketException or IOException => $"通信エラー：{error.Message}",
        ObjectDisposedException => "接続が切れました",
        ArgumentException or FormatException => $"アドレスまたは値が不正：{error.Message}",
        _ => error.Message,
    };
}
