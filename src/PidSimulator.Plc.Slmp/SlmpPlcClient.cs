using System.Diagnostics;
using System.Net.Sockets;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;
using PlcComm.Slmp;

namespace PidSimulator.Plc.Slmp;

/// <summary>SLMP のランダム読込・書込を使う実機PLCクライアント。</summary>
public sealed class SlmpPlcClient : CachedPlcClient<SlmpClient>
{
    private readonly SlmpConnectionOptions _options;

    public SlmpPlcClient(PlcSettings settings) : base(settings, EndpointText(settings.Effective()))
    {
        _options = CreateOptions(Settings);
    }

    private static string EndpointText(PlcSettings settings) => settings.UseGxSimulator
        ? $"GX Simulator 3（{settings.Host}:{settings.Port}・{DisplayName(settings.Profile)}・{ModuleIoDisplayName(settings.SlmpModuleIo)}）"
        : $"SLMP {settings.Host}:{settings.Port}/{(settings.Udp ? "UDP" : "TCP")}（{DisplayName(settings.Profile)}・{ModuleIoDisplayName(settings.SlmpModuleIo)}）";

    protected override Task<SlmpClient> OpenClientAsync(CancellationToken ct) =>
        SlmpClientFactory.OpenAndConnectAsync(_options, ct);

    protected override async Task<IReadOnlyDictionary<string, object>> ReadValuesAsync(SlmpClient client, string[] keys, CancellationToken ct) =>
        await client.ReadNamedAsync(keys, ct).ConfigureAwait(false);

    protected override Task WriteValuesAsync(SlmpClient client, IReadOnlyDictionary<string, object> updates, CancellationToken ct) =>
        client.WriteNamedAsync(updates, ct);

    protected override Task WriteValueAsync(SlmpClient client, string address, string suffix, double raw, CancellationToken ct) =>
        client.WriteTypedAsync(address, suffix, ToTyped(suffix, raw), ct);

    protected override bool IsPointError(Exception ex) =>
        ex is SlmpError { EndCode: not null } and not SlmpOperationOutcomeUnknownException
        || ex is FormatException or ArgumentException or SlmpProfileFeatureException;

    protected override string DescribeError(Exception ex) => Describe(ex);

    private static SlmpConnectionOptions CreateOptions(PlcSettings s) =>
        new(s.Host.Trim(), SlmpPlcProfiles.Parse(s.Profile), s.Port, s.Udp ? SlmpTransportMode.Udp : SlmpTransportMode.Tcp,
            new SlmpTargetAddress(0x00, 0xFF, ResolveModuleIo(s.SlmpModuleIo), 0x00))
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(1, s.TimeoutMs)),
        };

    private static ushort ResolveModuleIo(SlmpModuleIoTarget target) => target switch
    {
        SlmpModuleIoTarget.OwnStation => SlmpModuleIo.OwnStation,
        SlmpModuleIoTarget.ControlSystemCpu => SlmpModuleIo.ControlSystemCpu,
        SlmpModuleIoTarget.StandbySystemCpu => SlmpModuleIo.StandbySystemCpu,
        SlmpModuleIoTarget.SystemACpu => SlmpModuleIo.SystemACpu,
        SlmpModuleIoTarget.SystemBCpu => SlmpModuleIo.SystemBCpu,
        SlmpModuleIoTarget.MultipleCpu1 => SlmpModuleIo.MultipleCpu1,
        SlmpModuleIoTarget.MultipleCpu2 => SlmpModuleIo.MultipleCpu2,
        SlmpModuleIoTarget.MultipleCpu3 => SlmpModuleIo.MultipleCpu3,
        SlmpModuleIoTarget.MultipleCpu4 => SlmpModuleIo.MultipleCpu4,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "SLMPの要求先CPUが不明です。"),
    };

    public static string ModuleIoDisplayName(SlmpModuleIoTarget target) => target switch
    {
        SlmpModuleIoTarget.OwnStation => "自局",
        SlmpModuleIoTarget.ControlSystemCpu => "制御系CPU",
        SlmpModuleIoTarget.StandbySystemCpu => "待機系CPU",
        SlmpModuleIoTarget.SystemACpu => "A系CPU",
        SlmpModuleIoTarget.SystemBCpu => "B系CPU",
        SlmpModuleIoTarget.MultipleCpu1 => "CPU 1",
        SlmpModuleIoTarget.MultipleCpu2 => "CPU 2",
        SlmpModuleIoTarget.MultipleCpu3 => "CPU 3",
        SlmpModuleIoTarget.MultipleCpu4 => "CPU 4",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "SLMPの要求先CPUが不明です。"),
    };

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
                ? "\nGX Works3 でシミュレーションを開始しているか、パラメータ「RUN中の書込み許可/禁止設定：一括で許可する(SLMP)」が設定されているかを確認してください。"
                : "";
            return new PlcTestResult(false, 0, $"接続できません：{Describe(ex)}{hint}", sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>接続できるPLCプロファイル（正規名・表示名）</summary>
    public static IReadOnlyList<(string Name, string DisplayName)> Profiles() =>
        SlmpPlcProfiles.GetProfileDescriptors().Where(d => d.Connectable).Select(d => (d.CanonicalName, d.DisplayName)).ToList();

    public static string DisplayName(string canonical) =>
        SlmpPlcProfiles.GetProfileDescriptors().FirstOrDefault(d => d.CanonicalName == canonical)?.DisplayName ?? canonical;

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
        0x0055 => "（RUN中の書込みが許可されていません。CPUパラメータ「RUN中の書込み許可/禁止設定」を「一括で許可する(SLMP)」にしてPLCへ書き込んでください）",
        0xC056 => "（デバイス番号がPLCのデバイス範囲外です）",
        0xC059 => "（このPLCでは使えないコマンドです。PLC機種の設定を確認してください）",
        0xC05C => "（要求内容が不正です。アドレスとデータ型を確認してください）",
        0xC061 => "（要求データ長が不正です）",
        _ when name != null && !name.StartsWith("slmp_end_code_", StringComparison.Ordinal) => $" {name}",
        _ => "",
    };

    protected override async Task<object> ReadValueAsync(SlmpClient client, string address, string suffix, CancellationToken ct)
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
            if (!PlcBitAddress.IsValid(address, Settings)) throw new ArgumentException("ビットデバイスまたはD1.0形式のワード内ビットを指定してください。");
            if (PlcBitAddress.TryWordBit(address, out var word, out int bit, Settings))
            {
                var value = await client.ReadTypedAsync(word, "U", ct).ConfigureAwait(false);
                return (Convert.ToInt32(value) >> bit) & 1;
            }
        }
        return await client.ReadTypedAsync(address, suffix, ct).ConfigureAwait(false);
    }

}
