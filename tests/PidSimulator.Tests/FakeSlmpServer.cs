using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace PidSimulator.Tests;

/// <summary>
/// テスト用の最小限のSLMPサーバ（バイナリ3E/4E、TCP）。
/// ワードデバイス D / SD のランダム読込・書込とダイレクト読込・書込に応答する。
/// デバイス番号が <see cref="BadFrom"/> 以上なら終了コード 0xC056（範囲外）を返す。
/// </summary>
public sealed class FakeSlmpServer : IAsyncDisposable
{
    public const ushort BadFrom = 30000;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<(ushort Code, uint Number), ushort> _words = new();
    private readonly ConcurrentBag<TcpClient> _clients = [];
    private readonly Task _acceptLoop;

    public FakeSlmpServer(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _acceptLoop = AcceptAsync(_cts.Token);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ConcurrentQueue<ushort> Commands { get; } = new();

    public int ConnectionCount;

    public short GetD(uint number) => (short)_words.GetValueOrDefault(((ushort)0xA8, number));

    public void SetD(uint number, short value) => _words[((ushort)0xA8, number)] = (ushort)value;
    public void SetBit(ushort code, uint number, bool value) => _words[(code, number)] = value ? (ushort)1 : (ushort)0;

    /// <summary>接続中のクライアントをすべて切断する（通信断のテスト用）</summary>
    public void DropConnections()
    {
        while (_clients.TryTake(out var c)) c.Dispose();
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch { return; }
            Interlocked.Increment(ref ConnectionCount);
            _clients.Add(client);
            _ = ServeAsync(client, ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            var s = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                var sub = await ReadExact(s, 2, ct);
                bool is4E = sub[0] == 0x54;
                var head = await ReadExact(s, is4E ? 11 : 7, ct);
                int lenOffset = is4E ? 9 : 5;
                int len = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(lenOffset));
                var body = await ReadExact(s, len, ct);
                ushort cmd = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2));
                ushort subcmd = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4));
                Commands.Enqueue(cmd);
                var (end, data) = Handle(cmd, subcmd, body.AsSpan(6).ToArray());

                // 応答：宛先（ネットワーク～マルチドロップ）はそのまま返す
                var route = is4E ? head.AsSpan(4, 5).ToArray() : head.AsSpan(0, 5).ToArray();
                var resp = new List<byte>();
                if (is4E) resp.AddRange([0xD4, 0x00, head[0], head[1], 0x00, 0x00]);
                else resp.AddRange([0xD0, 0x00]);
                resp.AddRange(route);
                var lenBytes = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(lenBytes, (ushort)(2 + data.Length));
                resp.AddRange(lenBytes);
                resp.Add((byte)(end & 0xFF));
                resp.Add((byte)(end >> 8));
                resp.AddRange(data);
                await s.WriteAsync(resp.ToArray(), ct);
            }
        }
        catch
        {
            // 切断・終了
        }
        finally
        {
            client.Dispose();
        }
    }

    private (ushort End, byte[] Data) Handle(ushort cmd, ushort sub, byte[] d)
    {
        int spec = (sub & 0x0002) != 0 ? 6 : 4;
        (ushort Code, uint Number) Dev(int o) => spec == 4
            ? (d[o + 3], (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16))
            : (BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o + 4)), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o)));
        bool Bad((ushort Code, uint Number) dev) => dev.Number >= BadFrom;
        ushort W((ushort, uint) dev) => _words.GetValueOrDefault(dev);

        switch (cmd)
        {
            case 0x0403: // ランダム読出し
            {
                int wc = d[0], dc = d[1];
                var devs = Enumerable.Range(0, wc + dc).Select(i => Dev(2 + i * spec)).ToList();
                if (devs.Any(Bad)) return (0xC056, []);
                var o = new List<byte>();
                foreach (var dev in devs.Take(wc)) o.AddRange(BitConverter.GetBytes(W(dev)));
                foreach (var dev in devs.Skip(wc))
                {
                    o.AddRange(BitConverter.GetBytes(W(dev)));
                    o.AddRange(BitConverter.GetBytes(W((dev.Code, dev.Number + 1))));
                }
                return (0, o.ToArray());
            }
            case 0x1402: // ランダム書込み
            {
                int wc = d[0], dc = d[1], o = 2;
                var pending = new List<((ushort, uint) Dev, ushort Value)>();
                for (int i = 0; i < wc; i++, o += spec + 2)
                    pending.Add((Dev(o), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o + spec))));
                for (int i = 0; i < dc; i++, o += spec + 4)
                {
                    var dev = Dev(o);
                    uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + spec));
                    pending.Add((dev, (ushort)v));
                    pending.Add(((dev.Code, dev.Number + 1), (ushort)(v >> 16)));
                }
                if (pending.Any(p => Bad(p.Dev))) return (0xC056, []);
                foreach (var (dev, value) in pending) _words[dev] = value;
                return (0, []);
            }
            case 0x0401: // 一括読出し（ワード単位）
            {
                var dev = Dev(0);
                int count = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(spec));
                if (Bad(dev)) return (0xC056, []);
                if ((sub & 1) != 0)
                {
                    var bits = new byte[(count + 1) / 2];
                    for (int i = 0; i < count; i++)
                        if (W((dev.Code, dev.Number + (uint)i)) != 0) bits[i / 2] |= (byte)(i % 2 == 0 ? 0x10 : 0x01);
                    return (0, bits);
                }
                var o = new List<byte>();
                for (uint i = 0; i < count; i++) o.AddRange(BitConverter.GetBytes(W((dev.Code, dev.Number + i))));
                return (0, o.ToArray());
            }
            case 0x1401: // 一括書込み（ワード単位）
            {
                var dev = Dev(0);
                int count = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(spec));
                if (Bad(dev)) return (0xC056, []);
                for (int i = 0; i < count; i++)
                    _words[(dev.Code, dev.Number + (uint)i)] = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(spec + 2 + i * 2));
                return (0, []);
            }
            default:
                return (0xC059, []);
        }
    }

    private static async Task<byte[]> ReadExact(NetworkStream s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = await s.ReadAsync(buf.AsMemory(got), ct);
            if (r == 0) throw new IOException("closed");
            got += r;
        }
        return buf;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        DropConnections();
        try { await _acceptLoop; } catch { }
        _cts.Dispose();
    }
}
