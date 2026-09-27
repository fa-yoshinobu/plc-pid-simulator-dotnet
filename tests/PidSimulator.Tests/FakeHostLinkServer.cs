using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace PidSimulator.Tests;

/// <summary>TCP/UDP の上位リンクを受け付けるテストPLC。RD/RDS/WR/WRS/?M に応答する。</summary>
public sealed class FakeHostLinkServer : IAsyncDisposable
{
    private readonly TcpListener? _listener;
    private readonly UdpClient? _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<(string Device, int Number), ushort> _words = new();
    private readonly ConcurrentDictionary<string, byte> _rejected = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<TcpClient> _clients = [];
    private readonly ConcurrentBag<Task> _sessions = [];
    private readonly Task _loop;

    public FakeHostLinkServer(bool udp = false)
    {
        if (udp)
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _loop = ServeUdpAsync(_cts.Token);
        }
        else
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = AcceptAsync(_cts.Token);
        }
    }

    public int Port { get; }
    public int ConnectionCount;
    public volatile bool DropResponses;
    public ConcurrentQueue<string> Commands { get; } = new();
    public void RejectAddress(string address, bool reject = true)
    {
        if (reject) _rejected[address] = 0;
        else _rejected.TryRemove(address, out _);
    }

    public void SetWord(string address, ushort value) => _words[Parse(address).Key] = value;
    public ushort GetWord(string address) => _words.GetValueOrDefault(Parse(address).Key);
    public void SetBit(string address, bool value) => SetWord(address, value ? (ushort)1 : (ushort)0);
    public void SetInt32(string address, int value)
    {
        var (device, number) = Parse(address).Key;
        _words[(device, number)] = unchecked((ushort)value);
        _words[(device, number + 1)] = unchecked((ushort)(value >> 16));
    }
    public int GetInt32(string address)
    {
        var (device, number) = Parse(address).Key;
        return unchecked((int)(_words.GetValueOrDefault((device, number)) | (uint)_words.GetValueOrDefault((device, number + 1)) << 16));
    }
    public void SetFloat(string address, float value) => SetInt32(address, BitConverter.SingleToInt32Bits(value));
    public float GetFloat(string address) => BitConverter.Int32BitsToSingle(GetInt32(address));

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
                var client = await _listener!.AcceptTcpClientAsync(ct);
                Interlocked.Increment(ref ConnectionCount);
                _clients.Add(client);
                _sessions.Add(ServeTcpAsync(client, ct));
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }

    private async Task ServeTcpAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[1024];
            var command = new StringBuilder();
            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == '\n') continue;
                    if (buffer[i] != '\r') { command.Append((char)buffer[i]); continue; }
                    string response = Handle(command.ToString());
                    command.Clear();
                    if (!DropResponses) await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "\r\n"), ct);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        finally { client.Dispose(); }
    }

    private async Task ServeUdpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var request = await _udp!.ReceiveAsync(ct);
                string response = Handle(Encoding.ASCII.GetString(request.Buffer).TrimEnd('\r', '\n'));
                if (!DropResponses) await _udp.SendAsync(Encoding.ASCII.GetBytes(response + "\r\n"), request.RemoteEndPoint, ct);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }

    private string Handle(string command)
    {
        Commands.Enqueue(command);
        if (command == "?M") return "1";
        var parts = command.Split(' ');
        if (parts.Length < 2) return "E1";
        var parsed = Parse(parts[1]);
        var (device, start) = parsed.Key;
        string suffix = parsed.Suffix;
        bool wide = suffix is "L" or "D";
        int count = parts[0] is "RDS" or "WRS" ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 1;
        for (int i = 0; i < count * (wide ? 2 : 1); i++)
            if (_rejected.ContainsKey(device + (start + i))) return "E0";
        if (parts[0] is "RD" or "RDS")
        {
            var values = new List<string>();
            for (int i = 0; i < count; i++)
            {
                int number = start + i * (wide ? 2 : 1);
                ushort lo = _words.GetValueOrDefault((device, number));
                uint dword = lo | (uint)_words.GetValueOrDefault((device, number + 1)) << 16;
                values.Add(suffix switch
                {
                    "S" => unchecked((short)lo).ToString(CultureInfo.InvariantCulture),
                    "L" => unchecked((int)dword).ToString(CultureInfo.InvariantCulture),
                    "D" => dword.ToString(CultureInfo.InvariantCulture),
                    _ => lo.ToString(CultureInfo.InvariantCulture),
                });
            }
            return string.Join(' ', values);
        }
        if (parts[0] is "WR" or "WRS")
        {
            int offset = parts[0] == "WRS" ? 3 : 2;
            for (int i = 0; i < count; i++)
            {
                long value = long.Parse(parts[offset + i], CultureInfo.InvariantCulture);
                int number = start + i * (wide ? 2 : 1);
                _words[(device, number)] = unchecked((ushort)value);
                if (wide) _words[(device, number + 1)] = unchecked((ushort)(value >> 16));
            }
            return "OK";
        }
        return "E1";
    }

    private static ((string Device, int Number) Key, string Suffix) Parse(string address)
    {
        var match = Regex.Match(address, @"^(DM|EM|FM|ZF|MR|LR|CR|CM|TM|VM|VB|R|B|W|X|Y|M|L|D|E|F)([0-9A-F]+)(?:\.([USLD]))?$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new FormatException($"Unsupported fake PLC address {address}");
        string device = match.Groups[1].Value;
        string number = match.Groups[2].Value;
        int index = device is "X" or "Y"
            ? int.Parse(number.Length == 1 ? "0" : number[..^1], CultureInfo.InvariantCulture) * 16
                + int.Parse(number[^1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(number, device is "B" or "W" or "VB" ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture);
        return ((device, index), match.Groups[3].Value);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener?.Stop();
        _udp?.Dispose();
        DropConnections();
        await _loop;
        await Task.WhenAll(_sessions);
        _cts.Dispose();
    }
}
