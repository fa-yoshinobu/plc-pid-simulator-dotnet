using System.Diagnostics;
using System.Net.Sockets;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;
using PlcComm.Slmp;

namespace PidSimulator.Plc.Slmp;

/// <summary>
/// PlcComm.Slmp を使った実機PLCクライアント。
/// 演算周期（100 ms）からの Read/Write はキャッシュに対して即座に返し、
/// 通信ループが通信周期ごとに MV・SP を一括読込（Random Read）、PV を一括書込（Random Write）する。
/// </summary>
public sealed class SlmpPlcClient : IPlcClient
{
    private const int ChunkSize = 32;
    private static readonly TimeSpan UnusedLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WriteRefresh = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BadPointRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private readonly Dictionary<string, ReadPoint> _reads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WritePoint> _writes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SlmpConnectionOptions _options;
    private readonly TimeSpan _cycle;
    private readonly TimeSpan _staleLimit;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private SlmpClient? _client;
    private PlcConnectionState _state = PlcConnectionState.Disconnected;
    private string? _lastError;
    private double _lastCycleMs;
    private long _errorCount;
    private int _disposed;

    public SlmpPlcClient(PlcSettings settings)
    {
        Settings = settings.Effective();
        _options = CreateOptions(Settings);
        _cycle = TimeSpan.FromMilliseconds(Math.Max(20, Settings.CommCycleMs));
        _staleLimit = TimeSpan.FromMilliseconds(Settings.TimeoutMs) + _cycle * 3;
        Endpoint = Settings.UseGxSimulator
            ? $"GX Simulator 3（{Settings.Host}:{Settings.Port}・{DisplayName(Settings.Profile)}）"
            : $"SLMP {Settings.Host}:{Settings.Port}/{(Settings.Udp ? "UDP" : "TCP")}（{DisplayName(Settings.Profile)}）";
    }

    /// <summary>ユーザーの接続操作で通信ループを開始する。</summary>
    public void Connect()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_loop != null) return;
            _state = PlcConnectionState.Connecting;
            _loop = Task.Run(() => LoopAsync(_cts.Token));
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
            if (_state == PlcConnectionState.Disconnected) return PlcIoStatus.Failed;
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
            if (_state != PlcConnectionState.Connected) return PlcIoStatus.Failed;
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
        var sw = Stopwatch.StartNew();
        var client = CurrentClient();
        try
        {
            await using var temporary = client == null
                ? await SlmpClientFactory.OpenAndConnectAsync(_options, ct).ConfigureAwait(false) : null;
            client ??= temporary!;
            object v = await ReadValueAsync(client, Normalize(address), PlcDataTypes.Suffix(dataType), ct).ConfigureAwait(false);
            return new PlcTestResult(true, ToRaw(v), "OK", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PlcTestResult(false, 0, Describe(ex), sw.Elapsed.TotalMilliseconds);
        }
    }

    public async Task<PlcTestResult> TestWriteAsync(string address, string dataType, double raw, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var client = CurrentClient();
        string suffix = PlcDataTypes.Suffix(dataType);
        try
        {
            double clamped = PlcDataTypes.Clamp(dataType, raw);
            await using var temporary = client == null
                ? await SlmpClientFactory.OpenAndConnectAsync(_options, ct).ConfigureAwait(false) : null;
            client ??= temporary!;
            await client.WriteTypedAsync(Normalize(address), suffix, ToTyped(suffix, clamped), ct).ConfigureAwait(false);
            return new PlcTestResult(true, clamped, "OK", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PlcTestResult(false, 0, Describe(ex), sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>設定画面の接続テスト。一時的に接続し、SD0（自己診断エラーコード）を読む。</summary>
    public static async Task<PlcTestResult> TestConnectionAsync(PlcSettings settings, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var client = await SlmpClientFactory.OpenAndConnectAsync(CreateOptions(settings.Effective()), ct).ConfigureAwait(false);
            ushort sd0 = await client.ReadLatestSelfDiagnosisErrorCodeAsync(ct).ConfigureAwait(false);
            return new PlcTestResult(true, sd0, $"接続できました（応答 {sw.Elapsed.TotalMilliseconds:0} ms、SD0 = 0x{sd0:X4}）", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string hint = settings.IsGxSimulator
                ? "\nGX Works3 でシミュレーションを開始しているか、パラメータ「オンライン変更の有効/無効：全て有効（SLMP）」が設定されているかを確認してください。"
                : "";
            return new PlcTestResult(false, 0, $"接続できません：{Describe(ex)}{hint}", sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>接続できるPLCプロファイル（正規名・表示名）</summary>
    public static IReadOnlyList<(string Name, string DisplayName)> Profiles() =>
        SlmpPlcProfiles.GetProfileDescriptors().Where(d => d.Connectable).Select(d => (d.CanonicalName, d.DisplayName)).ToList();

    public static string DisplayName(string canonical) =>
        SlmpPlcProfiles.GetProfileDescriptors().FirstOrDefault(d => d.CanonicalName == canonical)?.DisplayName ?? canonical;

    // ---- 通信ループ ----

    private async Task LoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            var client = CurrentClient();
            if (client == null)
            {
                SetState(PlcConnectionState.Connecting);
                try
                {
                    client = await SlmpClientFactory.OpenAndConnectAsync(_options, ct).ConfigureAwait(false);
                    lock (_lock)
                    {
                        _client = client;
                        _state = PlcConnectionState.Connected;
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
                    Fail($"接続できません：{Describe(ex)}", PlcConnectionState.Faulted);
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
                Fail(Describe(ex), PlcConnectionState.Faulted);
                await DropClientAsync().ConfigureAwait(false);
                if (!await DelayAsync(backoff, ct).ConfigureAwait(false)) break;
                continue;
            }

            var wait = _cycle - sw.Elapsed;
            if (wait > TimeSpan.Zero && !await DelayAsync(wait, ct).ConfigureAwait(false)) break;
        }
        await DropClientAsync().ConfigureAwait(false);
    }

    private async Task ExchangeAsync(SlmpClient client, CancellationToken ct)
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
                var result = await client.ReadNamedAsync(chunk.Select(p => p.Key).ToArray(), ct).ConfigureAwait(false);
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

        // PV の一括書込（変化した値と、1秒ごとのリフレッシュ）
        List<(WritePoint Point, double Raw)> due;
        lock (_lock)
        {
            due = _writes.Values
                .Where(p => p.Error == null || now - p.ErrorAt > BadPointRetry)
                .Where(p => p.Written != p.Raw || now - p.WrittenAt > WriteRefresh)
                .Select(p => (p, p.Raw)).ToList();
        }
        foreach (var chunk in due.Chunk(ChunkSize))
        {
            try
            {
                var updates = chunk.ToDictionary(x => x.Point.Key, x => ToTyped(x.Point.Suffix, x.Raw), StringComparer.OrdinalIgnoreCase);
                await client.WriteNamedAsync(updates, ct).ConfigureAwait(false);
                MarkWritten(chunk);
            }
            catch (Exception ex) when (IsPointError(ex))
            {
                foreach (var x in chunk) await WriteSingleAsync(client, x.Point, x.Raw, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ReadSingleAsync(SlmpClient client, ReadPoint p, CancellationToken ct)
    {
        try
        {
            object v = await ReadValueAsync(client, p.Address, p.Suffix, ct).ConfigureAwait(false);
            lock (_lock) { p.Raw = ToRaw(v); p.UpdatedAt = DateTime.UtcNow; p.Error = null; }
        }
        catch (Exception ex) when (IsPointError(ex))
        {
            lock (_lock) { p.Error = Describe(ex); p.ErrorAt = DateTime.UtcNow; _lastError = $"{p.Address}：{p.Error}"; _errorCount++; }
        }
    }

    private async Task WriteSingleAsync(SlmpClient client, WritePoint p, double raw, CancellationToken ct)
    {
        try
        {
            await client.WriteTypedAsync(p.Address, p.Suffix, ToTyped(p.Suffix, raw), ct).ConfigureAwait(false);
            MarkWritten([(p, raw)]);
        }
        catch (Exception ex) when (IsPointError(ex))
        {
            lock (_lock) { p.Error = Describe(ex); p.ErrorAt = DateTime.UtcNow; _lastError = $"{p.Address}：{p.Error}"; _errorCount++; }
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

    /// <summary>PLCが終了コードで拒否した、またはアドレスが不正：その点だけの問題で、接続は生きている</summary>
    private static bool IsPointError(Exception ex) =>
        ex is SlmpError { EndCode: not null } and not SlmpOperationOutcomeUnknownException
        || ex is FormatException or ArgumentException or SlmpProfileFeatureException;

    // ---- 補助 ----

    private static SlmpConnectionOptions CreateOptions(PlcSettings s) =>
        new(s.Host.Trim(), SlmpPlcProfiles.Parse(s.Profile), s.Port, s.Udp ? SlmpTransportMode.Udp : SlmpTransportMode.Tcp, SlmpTargetAddress.OwnStation)
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(1, s.TimeoutMs)),
        };

    private SlmpClient? CurrentClient()
    {
        lock (_lock) return _client;
    }

    private void SetState(PlcConnectionState state)
    {
        lock (_lock) _state = state;
    }

    private void Fail(string message, PlcConnectionState state)
    {
        lock (_lock)
        {
            _lastError = message;
            _errorCount++;
            _state = state;
        }
    }

    private async Task DropClientAsync()
    {
        SlmpClient? c;
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

    public static string Describe(Exception ex) => ex switch
    {
        SlmpOperationOutcomeUnknownException u => $"書込み結果が不明（{u.Reason}）",
        SlmpError { EndCode: { } code } e => $"PLCエラー 0x{code:X4}{EndCodeHint(code, e.EndCodeName)}",
        SlmpTimeoutException => "応答タイムアウト",
        SlmpTransportException t => $"通信エラー：{(t.InnerException ?? t).Message}",
        SlmpNotConnectedException or SlmpConnectionClosedException => "接続が切れました",
        SocketException s => $"通信エラー：{s.Message}",
        FormatException or ArgumentException => $"アドレスまたは値が不正：{ex.Message}",
        _ => ex.Message,
    };

    /// <summary>よく出る終了コードに対処方法を添える</summary>
    private static string EndCodeHint(ushort code, string? name) => code switch
    {
        0x0055 => "（RUN中の書込みが許可されていません。CPUパラメータ「オンライン変更の有効/無効」を「全て有効（SLMP）」にしてPLCへ書き込んでください）",
        0xC056 => "（デバイス番号がPLCのデバイス範囲外です）",
        0xC059 => "（このPLCでは使えないコマンドです。PLC機種の設定を確認してください）",
        0xC05C => "（要求内容が不正です。アドレスとデータ型を確認してください）",
        0xC061 => "（要求データ長が不正です）",
        _ when name != null && !name.StartsWith("slmp_end_code_", StringComparison.Ordinal) => $" {name}",
        _ => "",
    };

    private static string Normalize(string address) => address.Trim().ToUpperInvariant();

    private static async Task<object> ReadValueAsync(SlmpClient client, string address, string suffix, CancellationToken ct)
    {
        if (suffix == "L")
        {
            // PlcComm.Slmp 5.2.0 の直接読込は INT32 を float 経由にしてしまう。
            // 型を保つ名前付き読込を使い、専用ルートを要する長タイマ等だけ直接読込に任せる。
            string key = Key(address, suffix);
            try { return (await client.ReadNamedAsync([key], ct).ConfigureAwait(false))[key]; }
            catch (ArgumentException) { }
        }
        if (suffix == "BIT")
        {
            if (!PlcBitAddress.IsValid(address)) throw new ArgumentException("ビットデバイスまたはD1.0形式のワード内ビットを指定してください。");
            if (PlcBitAddress.TryWordBit(address, out var word, out int bit))
            {
                var value = await client.ReadTypedAsync(word, "U", ct).ConfigureAwait(false);
                return (Convert.ToInt32(value) >> bit) & 1;
            }
        }
        return await client.ReadTypedAsync(address, suffix, ct).ConfigureAwait(false);
    }

    private static string Key(string address, string suffix) => $"{Normalize(address)}:{suffix}";

    private static double ToRaw(object v)
    {
        double raw = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(raw)) throw new ArgumentException("PLCから有限値ではないRAW値を受信しました。");
        return raw;
    }

    // 各アームを object にしておく（しないと switch 式の共通型が float になり、整数型の書込みが拒否される）
    private static object ToTyped(string suffix, double raw) => suffix switch
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
