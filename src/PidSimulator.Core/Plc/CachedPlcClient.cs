using System.Diagnostics;
using PidSimulator.Core.Project;

namespace PidSimulator.Core.Plc;

/// <summary>通信方式に共通の非ブロッキングキャッシュ、再接続、点単位エラー分離。</summary>
public abstract class CachedPlcClient<TClient> : IConnectablePlcClient where TClient : class, IAsyncDisposable
{
    private const int ChunkSize = 32;
    private static readonly TimeSpan UnusedLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WriteRefresh = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BadPointRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private readonly Dictionary<string, ReadPoint> _reads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WritePoint> _writes = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _cycle;
    private readonly TimeSpan _staleLimit;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private TClient? _client;
    private PlcConnectionState _state = PlcConnectionState.Disconnected;
    private string? _lastError;
    private double _lastCycleMs;
    private long _errorCount;
    private int _disposed;

    protected CachedPlcClient(PlcSettings settings, string endpoint)
    {
        Settings = settings.Effective();
        _cycle = TimeSpan.FromMilliseconds(Math.Max(20, Settings.CommCycleMs));
        _staleLimit = TimeSpan.FromMilliseconds(Settings.TimeoutMs) + _cycle * 3;
        Endpoint = endpoint;
    }

    protected abstract Task<TClient> OpenClientAsync(CancellationToken ct);
    protected abstract Task<object> ReadValueAsync(TClient client, string address, string suffix, CancellationToken ct);
    protected abstract Task WriteValueAsync(TClient client, string address, string suffix, double raw, CancellationToken ct);
    protected abstract Task<IReadOnlyDictionary<string, object>> ReadValuesAsync(TClient client, string[] keys, CancellationToken ct);
    protected abstract Task WriteValuesAsync(TClient client, IReadOnlyDictionary<string, object> updates, CancellationToken ct);
    protected abstract bool IsPointError(Exception error);
    protected abstract string DescribeError(Exception error);
    protected virtual int WriteBatchSize => ChunkSize;
    protected virtual bool IsClientUsable(TClient client) => true;

    /// <summary>ユーザーの接続操作で通信ループを開始する。</summary>
    public void Connect()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_loop != null) return;
            _state = PlcConnectionState.Connecting;
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token));
        }
    }

    public async Task<PlcTestResult> ConnectAndWaitAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linked.Token.ThrowIfCancellationRequested();
        Connect();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < Settings.TimeoutMs + 1000L)
        {
            linked.Token.ThrowIfCancellationRequested();
            var status = Status;
            if (status.State == PlcConnectionState.Connected)
                return new PlcTestResult(true, 0, "接続しました", sw.Elapsed.TotalMilliseconds);
            if (status.State == PlcConnectionState.Faulted)
                return new PlcTestResult(false, 0, status.LastError ?? "接続できません", sw.Elapsed.TotalMilliseconds);
            await Task.Delay(25, linked.Token).ConfigureAwait(false);
        }
        return new PlcTestResult(false, 0, "接続がタイムアウトしました。通信設定と接続先を確認してください。", sw.Elapsed.TotalMilliseconds);
    }

    public PlcSettings Settings { get; }

    public string Endpoint { get; }

    public PlcStatus Status
    {
        get { lock (_lock) return new PlcStatus(_state, Endpoint, _lastError, _lastCycleMs, _errorCount); }
    }

    // ---- 演算周期から呼ばれる（ブロックしない） ----

    public PlcIoStatus Read(string address, string dataType, out double raw)
    {
        raw = 0;
        var now = DateTime.UtcNow;
        string suffix = PlcDataTypes.Suffix(dataType);
        string key = Key(address, suffix);
        lock (_lock)
        {
            if (_disposed != 0 || _state == PlcConnectionState.Disconnected) return PlcIoStatus.Failed;
            if (!_reads.TryGetValue(key, out var p))
            {
                _reads[key] = new ReadPoint(key, Normalize(address), suffix) { RegisteredAt = now, LastUsedAt = now };
                return PlcIoStatus.Pending;
            }
            p.LastUsedAt = now;
            if (p.Error != null) return PlcIoStatus.Failed;
            if (p.UpdatedAt is { } updated && now - updated <= _staleLimit)
            {
                raw = p.Raw;
                return PlcIoStatus.Ok;
            }
            return p.UpdatedAt == null && now - p.RegisteredAt <= _staleLimit ? PlcIoStatus.Pending : PlcIoStatus.Failed;
        }
    }

    public PlcIoStatus Write(string address, string dataType, double raw)
    {
        if (!PlcDataTypes.CanWrite(dataType, raw)) return PlcIoStatus.Failed;
        string suffix = PlcDataTypes.Suffix(dataType);
        string key = Key(address, suffix);
        lock (_lock)
        {
            if (_disposed != 0 || _state != PlcConnectionState.Connected) return PlcIoStatus.Failed;
            if (!_writes.TryGetValue(key, out var p))
            {
                p = new WritePoint(key, Normalize(address), suffix);
                _writes[key] = p;
            }
            p.Raw = PlcDataTypes.Clamp(dataType, raw);
            p.SetAt = DateTime.UtcNow;
            return p.Error != null ? PlcIoStatus.Failed : PlcIoStatus.Ok;
        }
    }

    // ---- 接続テスト（登録画面・共通設定） ----

    public async Task<PlcTestResult> TestReadAsync(string address, string dataType, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var sw = Stopwatch.StartNew();
        var client = CurrentClient();
        try
        {
            await using var temporary = client == null
                ? await OpenClientAsync(ct).ConfigureAwait(false) : null;
            client ??= temporary!;
            object v = await ReadValueAsync(client, Normalize(address), PlcDataTypes.Suffix(dataType), ct).ConfigureAwait(false);
            return new PlcTestResult(true, ToRaw(v), "OK", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PlcTestResult(false, 0, DescribeError(ex), sw.Elapsed.TotalMilliseconds);
        }
    }

    public async Task<PlcTestResult> TestWriteAsync(string address, string dataType, double raw, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var sw = Stopwatch.StartNew();
        var client = CurrentClient();
        string suffix = PlcDataTypes.Suffix(dataType);
        try
        {
            double clamped = PlcDataTypes.Clamp(dataType, raw);
            await using var temporary = client == null
                ? await OpenClientAsync(ct).ConfigureAwait(false) : null;
            client ??= temporary!;
            await WriteValueAsync(client, Normalize(address), suffix, clamped, ct).ConfigureAwait(false);
            return new PlcTestResult(true, clamped, "OK", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PlcTestResult(false, 0, DescribeError(ex), sw.Elapsed.TotalMilliseconds);
        }
    }

    // ---- 通信ループ ----

    private async Task LoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            var client = CurrentClient();
            // 手動テストで通信が中断された場合も、登録点のない待機中から再接続する。
            if (client != null && !IsClientUsable(client))
            {
                Fail("接続が切れました。再接続します。", PlcConnectionState.Faulted);
                await DropClientAsync().ConfigureAwait(false);
                client = null;
            }
            if (client == null)
            {
                SetState(PlcConnectionState.Connecting);
                try
                {
                    client = await OpenClientAsync(ct).ConfigureAwait(false);
                    lock (_lock)
                    {
                        _client = client;
                        _state = _disposed != 0 ? PlcConnectionState.Disconnected : PlcConnectionState.Connected;
                        _lastError = null;
                    }
                    backoff = TimeSpan.FromSeconds(1);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Fail($"接続できません：{DescribeError(ex)}", PlcConnectionState.Faulted);
                    if (!await DelayAsync(backoff, ct).ConfigureAwait(false)) break;
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                    continue;
                }
            }

            var sw = Stopwatch.StartNew();
            try
            {
                await ExchangeAsync(client, ct).ConfigureAwait(false);
                lock (_lock) _lastCycleMs = sw.Elapsed.TotalMilliseconds;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // タイムアウト・切断など：接続を捨てて再接続する。PV は毎周期上書きする値なので、次の接続で最新値を書き直す。
                Fail(DescribeError(ex), PlcConnectionState.Faulted);
                await DropClientAsync().ConfigureAwait(false);
                if (!await DelayAsync(backoff, ct).ConfigureAwait(false)) break;
                continue;
            }

            var wait = _cycle - sw.Elapsed;
            if (wait > TimeSpan.Zero && !await DelayAsync(wait, ct).ConfigureAwait(false)) break;
        }
        await DropClientAsync().ConfigureAwait(false);
    }

    private async Task ExchangeAsync(TClient client, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        List<ReadPoint> batch, retry;
        lock (_lock)
        {
            foreach (var k in _reads.Where(p => now - p.Value.LastUsedAt > UnusedLifetime).Select(p => p.Key).ToList()) _reads.Remove(k);
            foreach (var k in _writes.Where(p => now - p.Value.SetAt > UnusedLifetime).Select(p => p.Key).ToList()) _writes.Remove(k);
            batch = _reads.Values.Where(p => p.Error == null).ToList();
            retry = _reads.Values.Where(p => p.Error != null && now - p.ErrorAt > BadPointRetry).ToList();
        }

        // MV・SP の一括読込
        foreach (var p in batch.Where(p => p.Suffix == "BIT")) await ReadSingleAsync(client, p, ct).ConfigureAwait(false);
        foreach (var chunk in batch.Where(p => p.Suffix != "BIT").Chunk(ChunkSize))
        {
            try
            {
                var result = await ReadValuesAsync(client, chunk.Select(p => p.Key).ToArray(), ct).ConfigureAwait(false);
                var t = DateTime.UtcNow;
                lock (_lock)
                {
                    foreach (var p in chunk)
                    {
                        if (result.TryGetValue(p.Key, out var v)) { p.Raw = ToRaw(v); p.UpdatedAt = t; }
                    }
                }
            }
            catch (Exception ex) when (IsPointError(ex))
            {
                // 1点の不正アドレスで全体が止まらないよう、1点ずつ読んで原因の点だけ異常にする
                foreach (var p in chunk) await ReadSingleAsync(client, p, ct).ConfigureAwait(false);
            }
        }
        foreach (var p in retry) await ReadSingleAsync(client, p, ct).ConfigureAwait(false);

        // PV の書込（変化した値と、1秒ごとのリフレッシュ。まとめ方は通信方式ごとに指定）
        List<(WritePoint Point, double Raw)> due;
        lock (_lock)
        {
            due = _writes.Values
                .Where(p => p.Error == null || now - p.ErrorAt > BadPointRetry)
                .Where(p => p.Written != p.Raw || now - p.WrittenAt > WriteRefresh)
                .Select(p => (p, p.Raw)).ToList();
        }
        foreach (var chunk in due.Chunk(WriteBatchSize))
        {
            try
            {
                var updates = chunk.ToDictionary(x => x.Point.Key, x => ToTyped(x.Point.Suffix, x.Raw), StringComparer.OrdinalIgnoreCase);
                await WriteValuesAsync(client, updates, ct).ConfigureAwait(false);
                MarkWritten(chunk);
            }
            catch (Exception ex) when (IsPointError(ex))
            {
                foreach (var x in chunk) await WriteSingleAsync(client, x.Point, x.Raw, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ReadSingleAsync(TClient client, ReadPoint p, CancellationToken ct)
    {
        try
        {
            object v = await ReadValueAsync(client, p.Address, p.Suffix, ct).ConfigureAwait(false);
            lock (_lock) { p.Raw = ToRaw(v); p.UpdatedAt = DateTime.UtcNow; p.Error = null; }
        }
        catch (Exception ex) when (IsPointError(ex))
        {
            lock (_lock) { p.Error = DescribeError(ex); p.ErrorAt = DateTime.UtcNow; _lastError = $"{p.Address}：{p.Error}"; _errorCount++; }
        }
    }

    private async Task WriteSingleAsync(TClient client, WritePoint p, double raw, CancellationToken ct)
    {
        try
        {
            await WriteValueAsync(client, p.Address, p.Suffix, raw, ct).ConfigureAwait(false);
            MarkWritten([(p, raw)]);
        }
        catch (Exception ex) when (IsPointError(ex))
        {
            lock (_lock) { p.Error = DescribeError(ex); p.ErrorAt = DateTime.UtcNow; _lastError = $"{p.Address}：{p.Error}"; _errorCount++; }
        }
    }

    private void MarkWritten(IEnumerable<(WritePoint Point, double Raw)> written)
    {
        var t = DateTime.UtcNow;
        lock (_lock)
        {
            foreach (var (p, raw) in written) { p.Written = raw; p.WrittenAt = t; p.Error = null; }
        }
    }

    private TClient? CurrentClient()
    {
        lock (_lock) return _client;
    }

    private void SetState(PlcConnectionState state)
    {
        lock (_lock) _state = _disposed != 0 ? PlcConnectionState.Disconnected : state;
    }

    private void Fail(string message, PlcConnectionState state)
    {
        lock (_lock)
        {
            _lastError = message;
            _errorCount++;
            _state = _disposed != 0 ? PlcConnectionState.Disconnected : state;
        }
    }

    private async Task DropClientAsync()
    {
        TClient? c;
        lock (_lock)
        {
            c = _client;
            _client = null;
            if (_state == PlcConnectionState.Connected) _state = PlcConnectionState.Faulted;
        }
        if (c != null)
        {
            try { await c.DisposeAsync().ConfigureAwait(false); } catch { /* 破棄時の例外は無視 */ }
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    protected static string Normalize(string address) => address.Trim().ToUpperInvariant();

    protected static string Key(string address, string suffix) => $"{Normalize(address)}:{suffix}";

    private static double ToRaw(object v)
    {
        double raw = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(raw)) throw new ArgumentException("PLCから有限値ではないRAW値を受信しました。");
        return raw;
    }

    // 各アームを object にしておく（しないと switch 式の共通型が float になり、整数型の書込みが拒否される）
    protected static object ToTyped(string suffix, double raw) => suffix switch
    {
        "BIT" => (object)(raw != 0),
        "U" => (object)(ushort)Math.Clamp(raw, ushort.MinValue, ushort.MaxValue),
        "L" => (object)(int)raw,
        "F" => (object)(float)raw,
        _ => (object)(short)Math.Clamp(raw, short.MinValue, short.MaxValue),
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_lock) _state = PlcConnectionState.Disconnected;
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { /* ループ終了時の例外は無視 */ }
        _cts.Dispose();
    }

    private sealed class ReadPoint(string key, string address, string suffix)
    {
        public string Key { get; } = key;
        public string Address { get; } = address;
        public string Suffix { get; } = suffix;
        public double Raw { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime RegisteredAt { get; set; }
        public DateTime LastUsedAt { get; set; }
        public string? Error { get; set; }
        public DateTime ErrorAt { get; set; }
    }

    private sealed class WritePoint(string key, string address, string suffix)
    {
        public string Key { get; } = key;
        public string Address { get; } = address;
        public string Suffix { get; } = suffix;
        public double Raw { get; set; }
        public double? Written { get; set; }
        public DateTime WrittenAt { get; set; }
        public DateTime SetAt { get; set; } = DateTime.UtcNow;
        public string? Error { get; set; }
        public DateTime ErrorAt { get; set; }
    }
}
