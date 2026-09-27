using System.Diagnostics;
using System.Net.Sockets;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;
using PlcComm.KvHostLink;

namespace PidSimulator.Plc.HostLink;

/// <summary>KEYENCE KV の上位リンク通信。MV・SP はまとめて読み、PV は点ごとに書く。</summary>
public sealed class HostLinkPlcClient : CachedPlcClient<KvHostLinkClient>
{
    private readonly KvHostLinkConnectionOptions _options;

    public HostLinkPlcClient(PlcSettings settings) : base(settings, EndpointText(settings.Effective()))
    {
        _options = CreateOptions(Settings);
    }

    private static string EndpointText(PlcSettings s) => s.IsKvSimulator
        ? $"KV STUDIO シミュレータ（{s.Host}:{s.Port}・{DisplayName(s.Profile)}）"
        : $"Host Link {s.Host}:{s.Port}/{(s.Udp ? "UDP" : "TCP")}（{DisplayName(s.Profile)}）";

    private static KvHostLinkConnectionOptions CreateOptions(PlcSettings s) =>
        new(s.Host.Trim(), s.Port, s.Udp ? HostLinkTransportMode.Udp : HostLinkTransportMode.Tcp,
            s.Profile, TimeSpan.FromMilliseconds(Math.Max(1, s.TimeoutMs)));

    protected override async Task<KvHostLinkClient> OpenClientAsync(CancellationToken ct)
    {
        var client = await KvHostLinkClientFactory.OpenAndConnectAsync(_options, ct).ConfigureAwait(false);
        try
        {
            // UDP のソケット作成だけで接続済みにしない。PLCの読取専用コマンドで応答を確認する。
            await client.ConfirmOperatingModeAsync(ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static IReadOnlyList<(string Name, string DisplayName)> Profiles() =>
        KvHostLinkPlcProfiles.GetProfileDescriptors().Where(d => d.Connectable)
            .Select(d => (d.CanonicalName, d.DisplayName)).ToList();

    public static string DisplayName(string canonical) =>
        KvHostLinkPlcProfiles.GetProfileDescriptors().FirstOrDefault(d => d.CanonicalName == canonical)?.DisplayName ?? canonical;

    /// <summary>運転モード（?M）を読み、Host Link の応答を確認する。運転状態は変更しない。</summary>
    public static async Task<PlcTestResult> TestConnectionAsync(PlcSettings settings, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var client = await KvHostLinkClientFactory.OpenAndConnectAsync(CreateOptions(settings.Effective()), ct).ConfigureAwait(false);
            var mode = await client.ConfirmOperatingModeAsync(ct).ConfigureAwait(false);
            return new PlcTestResult(true, (int)mode,
                $"接続できました（応答 {sw.Elapsed.TotalMilliseconds:0} ms、{(mode == KvPlcMode.Run ? "RUN" : "PROGRAM")}）",
                sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string hint = settings.IsKvSimulator ? "\nKV STUDIOでシミュレータと上位リンク通信を開始しているか確認してください。" : "";
            return new PlcTestResult(false, 0, $"接続できません：{Describe(ex)}{hint}", sw.Elapsed.TotalMilliseconds);
        }
    }

    protected override async Task<object> ReadValueAsync(KvHostLinkClient client, string address, string suffix, CancellationToken ct)
    {
        var parsed = Resolve(address, suffix);
        if (parsed.BitIndex is { } bit)
        {
            object word = await client.ReadTypedAsync(parsed.BaseAddress, "U", ct).ConfigureAwait(false);
            return (Convert.ToUInt16(word) & (1 << bit)) != 0;
        }
        return await client.ReadTypedAsync(parsed.BaseAddress, suffix, ct).ConfigureAwait(false);
    }

    protected override async Task<IReadOnlyDictionary<string, object>> ReadValuesAsync(KvHostLinkClient client, string[] keys, CancellationToken ct)
    {
        foreach (string key in keys)
        {
            int colon = key.LastIndexOf(':');
            Resolve(key[..colon], key[(colon + 1)..]);
        }
        return await client.ReadNamedAsync(keys, ct).ConfigureAwait(false);
    }

    // Host Link の一括書込みは連続アドレス限定。登録順や型の異なるPVはそれぞれ送る。
    protected override int WriteBatchSize => 1;

    protected override Task WriteValuesAsync(KvHostLinkClient client, IReadOnlyDictionary<string, object> updates, CancellationToken ct)
    {
        var point = updates.Single();
        int colon = point.Key.LastIndexOf(':');
        return WriteValueAsync(client, point.Key[..colon], point.Key[(colon + 1)..], Convert.ToDouble(point.Value), ct);
    }

    protected override Task WriteValueAsync(KvHostLinkClient client, string address, string suffix, double raw, CancellationToken ct)
    {
        var parsed = Resolve(address, suffix);
        if (parsed.BitIndex is { } bit)
            return client.WriteBitInWordAsync(parsed.BaseAddress, bit, raw != 0, ct);
        return suffix switch
        {
            "BIT" => client.WriteTypedAsync(parsed.BaseAddress, suffix, raw != 0, ct),
            "U" => client.WriteTypedAsync(parsed.BaseAddress, suffix, (ushort)raw, ct),
            "L" => client.WriteTypedAsync(parsed.BaseAddress, suffix, (int)raw, ct),
            "F" => client.WriteTypedAsync(parsed.BaseAddress, suffix, (float)raw, ct),
            _ => client.WriteTypedAsync(parsed.BaseAddress, suffix, (short)raw, ct),
        };
    }

    private HostLinkAddress Resolve(string address, string suffix)
    {
        string type = suffix switch { "BIT" => "BIT", "U" => "UINT16", "L" => "INT32", "F" => "FLOAT32", _ => "INT16" };
        if (!HostLinkAddressRules.TryResolve(address, type, Settings.Profile, out var parsed))
            throw new ArgumentException($"{DisplayName(Settings.Profile)}で使用できる{type}のアドレスを指定してください：{address}");
        return parsed;
    }

    protected override bool IsPointError(Exception ex) =>
        ex is HostLinkError { Code: not null } or HostLinkProtocolError or FormatException or ArgumentException;

    protected override string DescribeError(Exception ex) => Describe(ex);

    public static string Describe(Exception ex) => ex switch
    {
        HostLinkOutcomeUnknownError e => $"書込み結果が不明（{e.Reason}）",
        HostLinkTimeoutError => "応答タイムアウト",
        HostLinkNotConnectedError or HostLinkClosedError => "接続が切れました",
        HostLinkError { Code: { } code } => $"PLCエラー {code}（アドレス・データ型とPLCの設定を確認してください）",
        HostLinkConnectionError e => $"通信エラー：{(e.InnerException ?? e).Message}",
        SocketException e => $"通信エラー：{e.Message}",
        HostLinkProtocolError or FormatException or ArgumentException => $"アドレスまたは応答が不正：{ex.Message}",
        _ => ex.Message,
    };

}
