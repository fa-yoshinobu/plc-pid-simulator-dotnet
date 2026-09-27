using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace PidSimulator.Tests;

/// <summary>MBAPを直接解析するModbus TCPテスト機器。FC01/02/03/04/05/06/16に応答する。</summary>
public sealed class FakeModbusServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<(string Area, ushort Address), ushort> _values = new();
    private readonly ConcurrentDictionary<(string Area, ushort Address), byte> _rejected = new();
    private readonly ConcurrentBag<TcpClient> _clients = [];
    private readonly ConcurrentBag<Task> _sessions = [];
    private readonly Task _acceptLoop;

    public FakeModbusServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptAsync(_cts.Token);
    }

    public sealed record Request(ushort TransactionId, byte UnitId, byte Function, ushort Address, ushort Count, ushort[] Values);
    public int Port { get; }
    public int ConnectionCount;
    public int ActiveConnections;
    public volatile bool DropResponses;
    public volatile bool SplitResponses;
    public volatile bool WrongTransactionId;
    public volatile bool WrongWriteEcho;
    public volatile int DelayMs;
    public bool EthIoMapOnly { get; set; }
    public ConcurrentQueue<Request> Requests { get; } = new();

    public void SetWord(string area, ushort address, ushort value) => _values[(area, address)] = value;
    public ushort GetWord(string area, ushort address) => _values.GetValueOrDefault((area, address));
    public void SetBit(string area, ushort address, bool value) => SetWord(area, address, value ? (ushort)1 : (ushort)0);
    public void RejectAddress(string area, ushort address, bool reject = true)
    {
        if (reject) _rejected[(area, address)] = 0;
        else _rejected.TryRemove((area, address), out _);
    }

    public void DropConnections()
    {
        while (_clients.TryTake(out var client)) client.Dispose();
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                Interlocked.Increment(ref ConnectionCount);
                Interlocked.Increment(ref ActiveConnections);
                _clients.Add(client);
                _sessions.Add(ServeAsync(client, ct));
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                var header = new byte[7];
                await stream.ReadExactlyAsync(header, ct);
                int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                if (length is < 2 or > 254) throw new InvalidDataException("Invalid test request length.");
                var pdu = new byte[length - 1];
                await stream.ReadExactlyAsync(pdu, ct);
                ushort transaction = BinaryPrimitives.ReadUInt16BigEndian(header);
                byte[] reply = Handle(transaction, header[6], pdu);
                if (DropResponses) continue;
                int delay = DelayMs;
                if (delay > 0) await Task.Delay(delay, ct);
                var response = new byte[7 + reply.Length];
                BinaryPrimitives.WriteUInt16BigEndian(response, WrongTransactionId ? (ushort)(transaction + 1) : transaction);
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), (ushort)(reply.Length + 1));
                response[6] = header[6];
                reply.CopyTo(response, 7);
                if (SplitResponses)
                {
                    for (int offset = 0; offset < response.Length; offset += 2)
                    {
                        await stream.WriteAsync(response.AsMemory(offset, Math.Min(2, response.Length - offset)), ct);
                        await Task.Delay(1, ct);
                    }
                }
                else await stream.WriteAsync(response, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        finally
        {
            client.Dispose();
            Interlocked.Decrement(ref ActiveConnections);
        }
    }

    private byte[] Handle(ushort transaction, byte unit, byte[] pdu)
    {
        byte function = pdu[0];
        if (pdu.Length < 5) return [(byte)(function | 0x80), 3];
        ushort start = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
        ushort field = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
        ushort count = function is 5 or 6 ? (ushort)1 : field;
        string area = function switch { 1 or 5 => "C", 2 => "DI", 3 or 6 or 16 => "HR", 4 => "IR", _ => "" };
        ushort[] values = function is 5 or 6 ? [field]
            : function == 16 ? Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + i * 2))).ToArray() : [];
        Requests.Enqueue(new Request(transaction, unit, function, start, count, values));
        if (area.Length == 0) return [(byte)(function | 0x80), 1];
        if (count == 0 || (uint)start + count > 65536) return [(byte)(function | 0x80), 2];
        for (int i = 0; i < count; i++)
        {
            ushort address = (ushort)(start + i);
            if (_rejected.ContainsKey((area, address)) || EthIoMapOnly && !InEthMap(area, address))
                return [(byte)(function | 0x80), 2];
        }
        if (function is 1 or 2)
        {
            var response = new byte[2 + (count + 7) / 8];
            response[0] = function;
            response[1] = (byte)(response.Length - 2);
            for (int i = 0; i < count; i++)
                if (GetWord(area, (ushort)(start + i)) != 0) response[2 + i / 8] |= (byte)(1 << (i % 8));
            return response;
        }
        if (function is 3 or 4)
        {
            var response = new byte[2 + count * 2];
            response[0] = function;
            response[1] = (byte)(count * 2);
            for (int i = 0; i < count; i++)
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2 + i * 2), GetWord(area, (ushort)(start + i)));
            return response;
        }
        if (function == 5 && field is not (0 or 0xFF00)) return [0x85, 3];
        for (int i = 0; i < count; i++)
            SetWord(area, (ushort)(start + i), function == 5 ? (field == 0 ? (ushort)0 : (ushort)1) : values[i]);
        byte[] echo = pdu[..5];
        if (WrongWriteEcho) echo[4] ^= 1;
        return echo;
    }

    private static bool InEthMap(string area, ushort address) => area switch
    {
        "IR" => address <= 5,
        "HR" => address is 6 or 7,
        "C" or "DI" => address <= 7,
        _ => false,
    };

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        DropConnections();
        await _acceptLoop;
        await Task.WhenAll(_sessions);
        _cts.Dispose();
    }
}
