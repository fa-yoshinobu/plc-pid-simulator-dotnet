using PidSimulator.Core.Models;

namespace PidSimulator.Core.Project;

/// <summary>制御対象1件の登録内容（仕様 §5）。登録ウィザードとプロジェクトファイルで共通に使う。</summary>
public sealed class TargetConfig
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public ModelKind Kind { get; set; }
    public string MvAddress { get; set; } = "";
    public bool MvOnOff { get; set; }
    public string PvAddress { get; set; } = "";
    public string SpAddress { get; set; } = "";
    public bool UseSp { get; set; } = true;
    public string DataType { get; set; } = "INT16";
    public RangeDef MvRange { get; set; } = new();
    public RangeDef PvRange { get; set; } = new();
    public RangeDef SpRange { get; set; } = new();
    public double InitialPv { get; set; }
    public double InternalSp { get; set; }
    public StopPvMode StopPv { get; set; } = StopPvMode.Hold;
    public double StopPvValue { get; set; }
    public CommErrorAction OnCommError { get; set; } = CommErrorAction.StopSimulation;
    public double SafeMv { get; set; }
    public RecoverMode Recover { get; set; } = RecoverMode.Manual;
    public Dictionary<string, double> Params { get; set; } = [];

    /// <summary>モデルの標準値で埋めた登録内容</summary>
    public static TargetConfig Default(ModelKind kind)
    {
        var info = ModelCatalog.Get(kind);
        var c = new TargetConfig { Kind = kind };
        c.ApplyModelDefaults(info);
        return c;
    }

    /// <summary>モデル変更時：レンジ・初期値・パラメータをモデルの標準値に置き換える</summary>
    public void ApplyModelDefaults(ModelInfo info)
    {
        Kind = info.Kind;
        MvRange = new RangeDef { EngMin = 0, EngMax = 100, Unit = "%" };
        PvRange = new RangeDef { EngMin = info.PvMin, EngMax = info.PvMax, Unit = info.Unit };
        SpRange = PvRange.Clone();
        InitialPv = info.InitPv;
        InternalSp = info.DefaultSp;
        StopPvValue = info.InitPv;
        Params = info.Params.ToDictionary(p => p.Key, p => p.Default);
    }

    public TargetConfig Clone()
    {
        var c = (TargetConfig)MemberwiseClone();
        c.MvRange = MvRange.Clone();
        c.PvRange = PvRange.Clone();
        c.SpRange = SpRange.Clone();
        c.Params = new Dictionary<string, double>(Params);
        return c;
    }
}

public enum PlcMode { Dummy, Slmp }

/// <summary>PLC接続設定（仕様 §24）。Profile は PlcComm.Slmp の正規名（例: melsec:iq-r）で保存する。</summary>
public sealed class PlcSettings
{
    public PlcMode Mode { get; set; } = PlcMode.Dummy;
    public string Profile { get; set; } = "melsec:iq-r";
    public string Host { get; set; } = "192.168.3.39";
    public int Port { get; set; } = 1025;
    public bool Udp { get; set; }
    public int TimeoutMs { get; set; } = 1000;
    public int CommCycleMs { get; set; } = 100;

    /// <summary>GX Works3 の GX Simulator 3 に接続する（iQ-R / iQ-L のみ）。接続先は 127.0.0.1:5511 / TCP に固定。</summary>
    public bool UseGxSimulator { get; set; }

    public const string GxSimulatorHost = "127.0.0.1";
    public const int GxSimulatorPort = 5511;

    /// <summary>GX Simulator 3 が SLMP で応答する機種</summary>
    public static bool SupportsGxSimulator(string profile) => profile is "melsec:iq-r" or "melsec:iq-l";

    public bool IsGxSimulator => UseGxSimulator && SupportsGxSimulator(Profile);

    /// <summary>実際に接続する設定（GX Simulator 3 のときは接続先を固定値に置き換える）</summary>
    public PlcSettings Effective()
    {
        var c = Clone();
        if (!IsGxSimulator)
        {
            c.UseGxSimulator = false;
            return c;
        }
        c.Host = GxSimulatorHost;
        c.Port = GxSimulatorPort;
        c.Udp = false;
        return c;
    }

    public PlcSettings Clone() => (PlcSettings)MemberwiseClone();

    public bool SameConnection(PlcSettings o) =>
        Mode == o.Mode && Profile == o.Profile && Host == o.Host && Port == o.Port && Udp == o.Udp
        && TimeoutMs == o.TimeoutMs && CommCycleMs == o.CommCycleMs && IsGxSimulator == o.IsGxSimulator;
}

public sealed class DataSettings
{
    public string CsvFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PidSimulator", "records");
    public int TrendMinutes { get; set; } = TrendBuffer.DefaultRetentionMinutes;
    public bool ConfirmParamChangeWhileRunning { get; set; } = true;
}

/// <summary>設備構成一式（仕様 §13）。*.psim として JSON で保存する。</summary>
public sealed class ProjectDocument
{
    public const int CurrentFormat = 2;

    [System.Text.Json.Serialization.JsonRequired]
    public int FormatVersion { get; set; } = CurrentFormat;
    public string Name { get; set; } = "新規プロジェクト";
    public PlcSettings Plc { get; set; } = new();
    public DataSettings Data { get; set; } = new();
    public List<TargetConfig> Targets { get; set; } = [];
}
