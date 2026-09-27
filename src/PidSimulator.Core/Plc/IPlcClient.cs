namespace PidSimulator.Core.Plc;

/// <summary>1点の読込・書込の結果</summary>
public enum PlcIoStatus
{
    /// <summary>値が有効（読込）／書込を受け付けた</summary>
    Ok,

    /// <summary>通信ループがまだ最初の値を取得していない。異常ではない。</summary>
    Pending,

    /// <summary>通信できない、または応答が古すぎる</summary>
    Failed,
}

public enum PlcConnectionState { Disconnected, Connecting, Connected, Faulted }

public sealed record PlcStatus(PlcConnectionState State, string Endpoint, string? LastError, double LastCycleMs, long ErrorCount);

public sealed record PlcTestResult(bool Ok, double Raw, string Message, double ElapsedMs);

/// <summary>
/// PLC通信の抽象。シミュレータの演算周期から呼ばれるため、<see cref="Read"/> と <see cref="Write"/> は
/// ブロックしてはならない（実機クライアントは通信ループのキャッシュを返す）。
/// </summary>
public interface IPlcClient : IDisposable
{
    string Endpoint { get; }

    PlcStatus Status { get; }

    /// <param name="dataType">INT16 / UINT16 / INT32 / FLOAT32 / BIT</param>
    PlcIoStatus Read(string address, string dataType, out double raw);

    PlcIoStatus Write(string address, string dataType, double raw);

    /// <summary>演算周期ごとにエンジンから呼ばれる。実機クライアントでは何もしない。</summary>
    void Tick(double dt) { }

    /// <summary>接続テスト用の1点読込（登録画面から使う。キャッシュを経由しない）</summary>
    Task<PlcTestResult> TestReadAsync(string address, string dataType, CancellationToken ct = default);

    /// <summary>接続テスト用の1点書込（明示的な確認のあとでだけ呼ぶこと）</summary>
    Task<PlcTestResult> TestWriteAsync(string address, string dataType, double raw, CancellationToken ct = default);
}

/// <summary>明示的な接続操作で通信を開始する実機クライアント。</summary>
public interface IConnectablePlcClient : IPlcClient
{
    void Connect();
    Task<PlcTestResult> ConnectAndWaitAsync(CancellationToken ct = default);
}

public static class PlcDataTypes
{
    public static readonly string[] All = ["INT16", "UINT16", "INT32", "FLOAT32"];

    /// <summary>SLMP / Host Link ライブラリの型指定子（S/U/L/F/BIT）</summary>
    public static string Suffix(string dataType) => dataType switch
    {
        "BIT" => "BIT",
        "UINT16" => "U",
        "INT32" => "L",
        "FLOAT32" => "F",
        _ => "S",
    };

    /// <summary>送信可能な有限値か。FLOAT32の範囲を超える値は無限大へ変換しない。</summary>
    public static bool CanWrite(string dataType, double raw) => double.IsFinite(raw)
        && (dataType != "FLOAT32" || Math.Abs(raw) <= float.MaxValue);

    /// <summary>データ型の送信値へ変換。整数だけを丸め、FLOAT32は単精度の小数を保持する。</summary>
    public static double Clamp(string dataType, double raw)
    {
        if (!CanWrite(dataType, raw)) throw new ArgumentOutOfRangeException(nameof(raw), "データ型で送信できる有限のRAW値を指定してください。");
        return dataType switch
        {
            "BIT" => raw == 0 ? 0 : 1,
            "UINT16" => Math.Round(Math.Clamp(raw, ushort.MinValue, ushort.MaxValue)),
            "INT32" => Math.Round(Math.Clamp(raw, int.MinValue, int.MaxValue)),
            "FLOAT32" => (float)raw,
            _ => Math.Round(Math.Clamp(raw, short.MinValue, short.MaxValue)),
        };
    }

    /// <summary>登録レンジの端点に使える値か（整数型では小数端点を認めない）。</summary>
    public static bool IsValidRange(string dataType, RangeDef range)
    {
        if (!range.IsValid || !All.Contains(dataType)) return false;
        if (dataType == "FLOAT32")
            return CanWrite(dataType, range.RawMin) && CanWrite(dataType, range.RawMax)
                && (float)range.RawMin < (float)range.RawMax;
        double min = dataType == "UINT16" ? ushort.MinValue : dataType == "INT32" ? int.MinValue : short.MinValue;
        double max = dataType == "UINT16" ? ushort.MaxValue : dataType == "INT32" ? int.MaxValue : short.MaxValue;
        return range.RawMin >= min && range.RawMax <= max
            && range.RawMin == Math.Truncate(range.RawMin) && range.RawMax == Math.Truncate(range.RawMax);
    }
}
