using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
using PidSimulator.Core;
using PidSimulator.Core.Models;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;
using PidSimulator.Plc.HostLink;
using PidSimulator.Plc.Slmp;

namespace PidSimulator.App.ViewModels;

public enum RegistrationMode { New, Edit, Duplicate }

public sealed partial class StepItem(int index, string name) : ObservableObject
{
    public int Index { get; } = index;
    public int Number => Index + 1;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isReachable;
}

public sealed partial class RangeRowViewModel : ObservableObject
{
    public RangeRowViewModel(string label, string direction, RangeDef r, bool canEditUnit = true)
    {
        Label = label;
        Direction = direction;
        CanEditUnit = canEditUnit;
        _rawMin = r.RawMin;
        _rawMax = r.RawMax;
        _engMin = r.EngMin;
        _engMax = r.EngMax;
        _unit = r.Unit;
    }

    public string Label { get; }
    public string Direction { get; }
    public bool CanEditUnit { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview), nameof(IsValid))] private double _rawMin;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview), nameof(IsValid))] private double _rawMax;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview), nameof(IsValid))] private double _engMin;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview), nameof(IsValid))] private double _engMax;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview))] private string _unit;

    public RangeDef ToRange() => new() { RawMin = RawMin, RawMax = RawMax, EngMin = EngMin, EngMax = EngMax, Unit = Unit.Trim() };
    public bool IsValid => ToRange().IsValid;

    public string Preview
    {
        get
        {
            var r = ToRange();
            if (!r.IsValid) return "最小 < 最大 にしてください";
            double mid = r.RawMin + (r.RawMax - r.RawMin) / 2;
            return $"RAW {mid} → {r.ToEng(mid):0.##} {r.Unit}";
        }
    }
}

public sealed record TestRow(string Signal, string Address, string DataType, string Raw, string Eng, string Result, bool Ok);

/// <summary>制御対象の登録・編集・複製ウィザード（仕様 §5・§19・§22）</summary>
public sealed partial class RegistrationViewModel : ObservableObject
{
    private readonly IReadOnlyList<ControlTarget> _others;
    private readonly IPlcClient _plc;
    private readonly PlcSettings _plcSettings;
    private readonly TargetConfig _source;
    private readonly bool _loading;
    private bool _updatingMvUnits;

    public RegistrationViewModel(RegistrationMode mode, TargetConfig source, IEnumerable<ControlTarget> others, IPlcClient plc, string? editingName = null, PlcSettings? plcSettings = null)
    {
        _loading = true;
        Mode = mode;
        _source = source.Clone();
        _others = others.ToList();
        _plc = plc;
        _plcSettings = plcSettings?.Clone() ?? plc switch
        {
            HostLinkPlcClient hostLink => hostLink.Settings.Clone(),
            SlmpPlcClient slmp => slmp.Settings.Clone(),
            _ => new PlcSettings(),
        };
        EditingName = editingName;
        Models = new ListCollectionView(ModelCatalog.All.ToList());
        Models.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModelInfo.Category)));

        Steps = new ObservableCollection<StepItem>(StepNames.Select((n, i) => new StepItem(i, n)));
        _maxReached = mode == RegistrationMode.New ? 0 : StepNames.Length - 1;

        _name = source.Name;
        _description = source.Description;
        _selectedModel = ModelCatalog.Get(source.Kind);
        _mvAddress = source.MvAddress;
        _mvOnOff = source.MvOnOff;
        _pvAddress = source.PvAddress;
        _spAddress = source.SpAddress;
        _useSp = source.UseSp;
        _dataType = source.DataType;
        LoadModelValues(source);
        _loading = false;
        UpdateSteps();
    }

    public RegistrationMode Mode { get; }
    public string? EditingName { get; }
    public string WindowTitle => Mode switch
    {
        RegistrationMode.Edit => "登録内容の編集",
        RegistrationMode.Duplicate => "制御対象の複製",
        _ => "制御対象の登録",
    };
    public string SubTitle => Mode switch
    {
        RegistrationMode.Edit => $"{EditingName} の登録内容を変更します",
        RegistrationMode.Duplicate => $"{EditingName} の設定を引き継いでいます。名称とPLCアドレスを確認してください",
        _ => "モデルを選び、名称・レンジ・PLC通信を設定して制御対象を追加します",
    };
    public string FinishLabel => Mode == RegistrationMode.Edit ? "変更を保存" : "登録する";

    public static string[] StepNames { get; } = ["モデル選択", "基本設定", "レンジ設定", "PLC通信設定", "初期値・異常時", "確認"];
    public ObservableCollection<StepItem> Steps { get; }
    public string[] DataTypes { get; } = ["INT16", "UINT16", "INT32", "FLOAT32"];
    public IReadOnlyList<string> MvUnits => EngineeringUnits.GetMvUnits(SelectedModel.Kind, MvOnOff);
    public string[] LevelPvUnits { get; } = ["%", "mm"];
    public ICollectionView Models { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsFirstStep), nameof(IsLastStep), nameof(StepCaption))] private int _stepIndex;
    [ObservableProperty] private int _maxReached;
    [ObservableProperty] private string? _errorText;

    public bool IsFirstStep => StepIndex == 0;
    public bool IsLastStep => StepIndex == StepNames.Length - 1;
    public string StepCaption => $"ステップ {StepIndex + 1} / {StepNames.Length}";

    // ---- 2. 基本設定 ----
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _description;

    // ---- 1. モデル ----
    [ObservableProperty] private ModelInfo _selectedModel;
    public bool ModelChanged => Mode == RegistrationMode.Edit && SelectedModel.Kind != _source.Kind;

    partial void OnSelectedModelChanged(ModelInfo value)
    {
        if (_loading) return;
        var c = _source.Clone();
        c.ApplyModelDefaults(value);
        LoadModelValues(c);
        OnPropertyChanged(nameof(ModelChanged));
    }

    // ---- 4. PLC通信 ----
    private bool IsHostLink => _plcSettings.Mode == PlcMode.HostLink;
    private bool IsXym => IsHostLink && _plcSettings.Profile.EndsWith("-xym", StringComparison.Ordinal);
    public string PlcConnectionSummary => _plc.Endpoint;
    public string WordAddressHint => IsHostLink
        ? IsXym ? "アドレス例：D1000（XYM表記）。INT32・FLOAT32は連続する2ワードを使用します。"
            : "アドレス例：DM1000。INT32・FLOAT32は連続する2ワードを使用します。"
        : "アドレス例：D1000。INT32・FLOAT32は連続する2ワードを使用します。";
    public string RelayAddressHint => (IsHostLink
        ? IsXym ? "ON/OFFはM・Yなどのビット、またはD1.0形式を読み込みます。"
            : "ON/OFFはMR・Rなどのビット、またはDM1.0形式を読み込みます。"
        : "ON/OFFはM・Yなどのビット、またはD1.0形式を読み込みます。")
        + "OFF=0%、ON=100%として扱います。SSRの高速パルス制御は対象外です。";
    [ObservableProperty] private string _mvAddress;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DataTypeLabel))] private bool _mvOnOff;
    public string DataTypeLabel => MvOnOff ? "PV・SPデータ型" : "データ型";
    [ObservableProperty] private string _pvAddress;
    [ObservableProperty] private string _spAddress;
    [ObservableProperty] private bool _useSp;
    [ObservableProperty] private string _dataType;
    public ObservableCollection<TestRow> TestRows { get; } = [];
    [ObservableProperty] private string _testSummary = "未実行です。読込テストはPLCへ書き込みません。";
    [ObservableProperty] private bool _pvWriteAck;
    [ObservableProperty] private double _pvTestValue;
    [ObservableProperty] private string _pvTestResult = "未実施";

    // ---- 3. レンジ ----
    [ObservableProperty] private RangeRowViewModel _mvRange = null!;
    [ObservableProperty] private RangeRowViewModel _pvRange = null!;
    [ObservableProperty] private RangeRowViewModel _spRange = null!;
    [ObservableProperty] private string _mvUnit = "%";
    [ObservableProperty] private string _levelPvUnit = "%";
    public bool IsLevelModel => EngineeringUnits.IsLevel(SelectedModel.Kind);
    public double TankHeightMm => EngineeringUnits.TankHeight(SelectedModel.Kind, _source.Params);
    public string LevelUnitHint => IsLevelModel ? $"タンク高さ {TankHeightMm:G} mm が液面100%に相当します。高さ・直径は登録後の「条件から計算」で設定できます。" : "";
    public string MvScalingHint => MvOnOff ? "OFF = 0%、ON = 100%"
        : MvRange.Unit == "%" ? "MVは設定した%の値をそのまま操作量に使います。"
        : $"MV {MvRange.EngMin:G}～{MvRange.EngMax:G} {MvRange.Unit} を、操作量0～100%に換算します。";

    partial void OnMvUnitChanged(string value)
    {
        if (_updatingMvUnits) return;
        if (!EngineeringUnits.IsSupportedMvUnit(SelectedModel.Kind, value, MvOnOff))
        {
            MvUnit = "%";
            return;
        }
        if (MvRange is null || MvRange.Unit == value) return;
        double safePercent = EngineeringUnits.MvToPercent(MvRange.ToRange(), SafeMv);
        (double min, double max) = value switch
        {
            "Hz" => (0, 60),
            "rpm" => (0, _source.Params.GetValueOrDefault("nmax", 1800)),
            "kW" => (0, _source.Params.GetValueOrDefault(SelectedModel.Kind == ModelKind.Heater ? "pmax" : "cmax",
                ModelCatalog.Get(SelectedModel.Kind).Params.First(p => p.Key == (SelectedModel.Kind == ModelKind.Heater ? "pmax" : "cmax")).Default)),
            "V" => (0, 10),
            "mA" => (4, 20),
            _ => (0, 100),
        };
        MvRange.Unit = value;
        MvRange.EngMin = min;
        MvRange.EngMax = max;
        SafeMv = EngineeringUnits.MvFromPercent(MvRange.ToRange(), safePercent);
        OnPropertyChanged(nameof(MvScalingHint));
    }

    partial void OnLevelPvUnitChanged(string value)
    {
        if (!IsLevelModel || PvRange.Unit == value) return;
        double scale = value == "mm" ? TankHeightMm / 100 : 100 / TankHeightMm;
        foreach (var range in new[] { PvRange, SpRange })
        {
            range.EngMin *= scale;
            range.EngMax *= scale;
            range.Unit = value;
        }
        InitialPv *= scale;
        InitialSp *= scale;
        StopPvValue *= scale;
        PvTestValue *= scale;
        OnPropertyChanged(nameof(PvUnit));
    }

    partial void OnMvOnOffChanged(bool value)
    {
        RefreshMvUnitChoices(value ? "%" : MvUnit);
        if (value) SafeMv = SafeMv >= 50 ? 100 : 0;
        OnPropertyChanged(nameof(MvScalingHint));
    }

    private void RefreshMvUnitChoices(string selectedUnit)
    {
        // ItemsSourceの切替時にComboBoxが一時的に選択を解除しても、レンジを変更しない。
        _updatingMvUnits = true;
        try
        {
            OnPropertyChanged(nameof(MvUnits));
            MvUnit = EngineeringUnits.IsSupportedMvUnit(SelectedModel.Kind, selectedUnit, MvOnOff) ? selectedUnit : "%";
        }
        finally { _updatingMvUnits = false; }
        OnMvUnitChanged(MvUnit);
        OnPropertyChanged(nameof(MvUnit));
    }

    // ---- 5. 初期値・異常時 ----
    [ObservableProperty] private double _initialPv;
    [ObservableProperty] private double _initialSp;
    [ObservableProperty] private StopPvMode _stopPv;
    [ObservableProperty] private double _stopPvValue;
    [ObservableProperty] private CommErrorAction _onCommError;
    [ObservableProperty] private double _safeMv;
    [ObservableProperty] private RecoverMode _recover;
    public string PvUnit => PvRange.Unit;

    // ---- 6. 確認 ----
    public ObservableCollection<CheckResult> Checks { get; } = [];
    [ObservableProperty] private bool _hasWarnings;
    [ObservableProperty] private bool _hasBlockers;
    [ObservableProperty] private bool _acknowledged;
    [ObservableProperty] private string _summaryModel = "";
    [ObservableProperty] private string _summaryAddress = "";
    [ObservableProperty] private string _summaryRange = "";
    [ObservableProperty] private string _summaryInitial = "";
    [ObservableProperty] private string _summaryBehavior = "";
    public bool CanFinish => !HasBlockers && (!HasWarnings || Acknowledged);

    partial void OnAcknowledgedChanged(bool value) => OnPropertyChanged(nameof(CanFinish));

    public TargetConfig? Result { get; private set; }
    public event Action<bool>? CloseRequested;

    private void LoadModelValues(TargetConfig c)
    {
        MvRange = new RangeRowViewModel("MV", "PLC → シミュレータ", c.MvRange, canEditUnit: false);
        PvRange = new RangeRowViewModel("PV", "シミュレータ → PLC", c.PvRange, canEditUnit: !IsLevelModel);
        SpRange = new RangeRowViewModel("SP", "PLC → シミュレータ", c.SpRange, canEditUnit: !IsLevelModel);
        LevelPvUnit = c.PvRange.Unit;
        MvRange.PropertyChanged += (_, _) => OnPropertyChanged(nameof(MvScalingHint));
        PvRange.PropertyChanged += (_, _) => OnPropertyChanged(nameof(PvUnit));
        InitialPv = c.InitialPv;
        InitialSp = c.InternalSp;
        StopPv = c.StopPv;
        StopPvValue = c.StopPvValue;
        OnCommError = c.OnCommError;
        SafeMv = c.SafeMv;
        Recover = c.Recover;
        PvTestValue = c.InitialPv;
        _source.Params = new Dictionary<string, double>(c.Params);
        RefreshMvUnitChoices(c.MvRange.Unit);
        OnPropertyChanged(nameof(PvUnit));
        OnPropertyChanged(nameof(MvUnit));
        OnPropertyChanged(nameof(LevelPvUnit));
        OnPropertyChanged(nameof(IsLevelModel));
        OnPropertyChanged(nameof(LevelUnitHint));
        OnPropertyChanged(nameof(MvScalingHint));
    }

    public TargetConfig BuildConfig() => new()
    {
        Name = Name.Trim(),
        Description = Description.Trim(),
        Kind = SelectedModel.Kind,
        MvAddress = MvAddress.Trim().ToUpperInvariant(),
        MvOnOff = MvOnOff,
        PvAddress = PvAddress.Trim().ToUpperInvariant(),
        SpAddress = SpAddress.Trim().ToUpperInvariant(),
        UseSp = UseSp,
        DataType = DataType,
        MvRange = MvOnOff ? new RangeDef { RawMax = 1, EngMax = 100, Unit = "%" } : MvRange.ToRange(),
        PvRange = PvRange.ToRange(),
        SpRange = SpRange.ToRange(),
        InitialPv = InitialPv,
        InternalSp = InitialSp,
        StopPv = StopPv,
        StopPvValue = StopPvValue,
        OnCommError = OnCommError,
        SafeMv = SafeMv,
        Recover = Recover,
        Params = new Dictionary<string, double>(_source.Params),
    };

    // ---- ナビゲーション ----

    private bool ValidateStep(int step)
    {
        ErrorText = step switch
        {
            1 when string.IsNullOrWhiteSpace(Name) => "制御名称を入力してください。",
            2 when (!MvOnOff && !MvRange.IsValid) || !PvRange.IsValid || !SpRange.IsValid => "レンジは 最小 < 最大 にしてください。",
            3 when string.IsNullOrWhiteSpace(MvAddress) || string.IsNullOrWhiteSpace(PvAddress) => "MVアドレスとPVアドレスを入力してください。",
            3 when UseSp && string.IsNullOrWhiteSpace(SpAddress) => "SPアドレスを入力するか、「PLCからSPを読み込む」を外してください。",
            _ => null,
        };
        return ErrorText == null;
    }

    private void UpdateSteps()
    {
        foreach (var s in Steps)
        {
            s.IsCurrent = s.Index == StepIndex;
            s.IsDone = s.Index < StepIndex || (s.Index <= MaxReached && s.Index != StepIndex);
            s.IsReachable = s.Index <= MaxReached;
        }
        if (IsLastStep) RunChecks();
    }

    private void RunChecks()
    {
        var cfg = BuildConfig();
        Checks.Clear();
        foreach (var c in RegistrationValidator.Check(cfg, _others, _plcSettings)) Checks.Add(c);
        HasBlockers = Checks.Any(c => c.Level == CheckLevel.Block);
        HasWarnings = Checks.Any(c => c.Level == CheckLevel.Warn);
        Acknowledged = false;
        SummaryModel = $"{SelectedModel.Name}（MV: {SelectedModel.MvLabel} / PV: {SelectedModel.PvLabel}）";
        SummaryAddress = $"MV {cfg.MvAddress}　PV {cfg.PvAddress}　SP {(cfg.UseSp ? cfg.SpAddress : "未使用")}　（{cfg.DataType}）";
        SummaryRange = cfg.PvRange.ToString();
        SummaryInitial = $"PV {cfg.InitialPv:G} / SP {cfg.InternalSp:G} {cfg.PvRange.Unit}";
        SummaryBehavior = $"停止時 {Labels.Of(cfg.StopPv)}　／　通信異常時 {Labels.Of(cfg.OnCommError)}（復旧: {(cfg.Recover == RecoverMode.Auto ? "自動" : "手動")}）";
        OnPropertyChanged(nameof(CanFinish));
    }

    [RelayCommand]
    private void Next()
    {
        if (!ValidateStep(StepIndex)) return;
        StepIndex++;
        MaxReached = Math.Max(MaxReached, StepIndex);
        UpdateSteps();
    }

    [RelayCommand]
    private void Back()
    {
        ErrorText = null;
        StepIndex--;
        UpdateSteps();
    }

    [RelayCommand]
    private void GoTo(StepItem step)
    {
        if (!step.IsReachable || step.Index == StepIndex) return;
        if (step.Index > StepIndex && !ValidateStep(StepIndex)) return;
        ErrorText = null;
        StepIndex = step.Index;
        UpdateSteps();
    }

    [RelayCommand]
    private void Finish()
    {
        for (int i = 0; i < StepNames.Length - 1; i++)
        {
            if (!ValidateStep(i)) { StepIndex = i; UpdateSteps(); return; }
        }
        RunChecks();
        if (!CanFinish) return;
        Result = BuildConfig();
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    // ---- 接続テスト（§19） ----

    [ObservableProperty] private bool _testing;

    /// <summary>MV・SPを1点ずつ直接読む（PLCへは書き込まない）</summary>
    [RelayCommand]
    private async Task ReadTest()
    {
        Testing = true;
        TestRows.Clear();
        TestSummary = $"{_plc.Endpoint} から読み込んでいます…";
        try
        {
            TestRows.Add(await ReadRowAsync("MV", MvAddress, MvRange.ToRange()));
            if (UseSp) TestRows.Add(await ReadRowAsync("SP", SpAddress, SpRange.ToRange()));
            else TestRows.Add(new TestRow("SP", "—", DataType, "—", "—", "未使用", true));
            int ng = TestRows.Count(r => !r.Ok);
            TestSummary = ng == 0
                ? $"{_plc.Endpoint} から読み込めました。値とスケーリングを確認してください。"
                : $"{ng} 件の読込に失敗しました。アドレス・データ型・PLCの状態を確認してください。";
        }
        finally
        {
            Testing = false;
        }
    }

    private async Task<TestRow> ReadRowAsync(string signal, string address, RangeDef range)
    {
        string addr = address.Trim().ToUpperInvariant();
        if (!RegistrationValidator.IsAddress(addr, _plcSettings)) return new TestRow(signal, addr, DataType, "—", "—", "選択中のPLC機種では使えないアドレスです", false);
        bool relay = signal == "MV" && MvOnOff;
        string type = relay ? "BIT" : DataType;
        var r = await _plc.TestReadAsync(addr, type);
        if (!r.Ok) return new TestRow(signal, addr, type, "—", "—", r.Message, false);
        if (relay) return new TestRow(signal, addr, type, r.Raw.ToString(), r.Raw == 0 ? "OFF（0%）" : "ON（100%）", "OK", true);
        string eng = range.IsValid ? $"{range.ToEng(r.Raw):0.##} {range.Unit}" : "レンジ不正";
        return new TestRow(signal, addr, DataType, r.Raw.ToString("G9"), eng, $"OK（{r.ElapsedMs:0} ms）", true);
    }

    /// <summary>PVアドレスへ1回だけ書き込む。確認チェックは実行後も保持する。</summary>
    [RelayCommand]
    private async Task PvWriteTest()
    {
        if (!PvWriteAck) return;
        var range = PvRange.ToRange();
        string addr = PvAddress.Trim().ToUpperInvariant();
        if (!range.IsValid || !RegistrationValidator.IsAddress(addr, _plcSettings) || !double.IsFinite(PvTestValue))
        {
            PvTestResult = "アドレスまたはレンジを確認してください";
            return;
        }
        double raw = range.ToRaw(PvTestValue);
        PvTestResult = $"{addr} へ書き込んでいます…";
        var r = await _plc.TestWriteAsync(addr, DataType, raw);
        PvTestResult = r.Ok
            ? $"{addr} へ RAW {r.Raw:G9}（{PvTestValue:G} {range.Unit}）を書き込みました"
            : $"{addr} への書込みに失敗しました：{r.Message}";
    }
}
