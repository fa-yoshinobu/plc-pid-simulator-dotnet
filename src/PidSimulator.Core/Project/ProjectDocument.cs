using PidSimulator.Core.Models;

using PidSimulator.Core.Plc;

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

public enum PlcMode { Dummy, Slmp, HostLink, ModbusTcp }

public enum ModbusWordOrder { HighWordFirst, LowWordFirst }

/// <summary>PLC接続設定（仕様 §24）。Profile は通信ライブラリの正規名（例: melsec:iq-r、keyence:kv-8000）で保存する。</summary>
public sealed class PlcSettings
{
    public PlcMode Mode { get; set; } = PlcMode.Dummy;
    public string Profile { get; set; } = "melsec:iq-r";
    public string Host { get; set; } = "192.168.3.39";
    public int Port { get; set; } = 1025;
    public bool Udp { get; set; }
    public int TimeoutMs { get; set; } = 1000;
    public int CommCycleMs { get; set; } = 100;

    public int ModbusUnitId { get; set; } = 1;
    public ModbusWordOrder ModbusWordOrder { get; set; } = ModbusWordOrder.HighWordFirst;
    public string ModbusTestAddress { get; set; } = "IR0";

    /// <summary>SLMPの要求先CPU。ネットワーク番号0・局番FFのまま、Module I/Oだけを指定する。</summary>
    public SlmpModuleIoTarget SlmpModuleIo { get; set; } = SlmpModuleIoTarget.OwnStation;

    /// <summary>GX Works3 の GX Simulator 3 に接続する（iQ-R / iQ-L のみ）。接続先は 127.0.0.1:5511 / TCP に固定。</summary>
    public bool UseGxSimulator { get; set; }

    /// <summary>KV STUDIO のシミュレータに接続する。接続先は 127.0.0.1:8501 / TCP に固定。</summary>
    public bool UseKvSimulator { get; set; }

    public const string GxSimulatorHost = "127.0.0.1";
    public const int GxSimulatorPort = 5511;
    public const string KvSimulatorHost = "127.0.0.1";
    public const int KvSimulatorPort = 8501;

    /// <summary>GX Simulator 3 が SLMP で応答する機種</summary>
    public static bool SupportsGxSimulator(string profile) => profile is "melsec:iq-r" or "melsec:iq-l";

    public static bool SupportsKvSimulator(string profile) => profile is
        "keyence:kv-8000" or "keyence:kv-8000-xym" or "keyence:kv-x500" or "keyence:kv-x500-xym";

    public bool IsGxSimulator => Mode == PlcMode.Slmp && UseGxSimulator && SupportsGxSimulator(Profile);
    public bool IsKvSimulator => Mode == PlcMode.HostLink && UseKvSimulator && SupportsKvSimulator(Profile);

    /// <summary>実際に接続する設定（シミュレータのときは接続先を固定値に置き換える）</summary>
    public PlcSettings Effective()
    {
        var c = Clone();
        if (!TryValidateModbus(out string error)) throw new ArgumentException(error);
        c.UseGxSimulator = IsGxSimulator;
        c.UseKvSimulator = IsKvSimulator;
        if (Mode == PlcMode.ModbusTcp)
        {
            c.Udp = false;
        }
        else if (IsGxSimulator)
        {
            c.Host = GxSimulatorHost;
            c.Port = GxSimulatorPort;
            c.Udp = false;
        }
        else if (IsKvSimulator)
        {
            c.Host = KvSimulatorHost;
            c.Port = KvSimulatorPort;
            c.Udp = false;
        }
        return c;
    }

    public PlcSettings Clone() => (PlcSettings)MemberwiseClone();

    public bool TryValidateModbus(out string error)
    {
        error = "";
        if (Mode != PlcMode.ModbusTcp) return true;
        if (ModbusUnitId is < 0 or > 255) error = "ModbusのUnit IDは0～255で指定してください。";
        else if (!Enum.IsDefined(ModbusWordOrder)) error = "Modbusの32ビットワード順が不正です。";
        else if (!ModbusAddressRules.TryParse(ModbusTestAddress, out _))
            error = "Modbusの接続テスト読込先はC・DI・HR・IRに0～65535の番号を付けて指定してください（例: IR0）。";
        return error.Length == 0;
    }

    public bool SameConnection(PlcSettings o) =>
        Mode == o.Mode && Host == o.Host && Port == o.Port
        && TimeoutMs == o.TimeoutMs && CommCycleMs == o.CommCycleMs
        && (Mode == PlcMode.ModbusTcp
            ? ModbusUnitId == o.ModbusUnitId && ModbusWordOrder == o.ModbusWordOrder
                && string.Equals(ModbusTestAddress?.Trim(), o.ModbusTestAddress?.Trim(), StringComparison.OrdinalIgnoreCase)
            : Profile == o.Profile && Udp == o.Udp
                && (Mode != PlcMode.Slmp || SlmpModuleIo == o.SlmpModuleIo)
                && IsGxSimulator == o.IsGxSimulator && IsKvSimulator == o.IsKvSimulator);
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
