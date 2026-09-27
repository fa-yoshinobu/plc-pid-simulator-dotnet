using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PidSimulator.Core;
using PidSimulator.Core.Project;
using PidSimulator.Plc.Slmp;
using PidSimulator.Plc.HostLink;

namespace PidSimulator.App.ViewModels;

public sealed record ProfileItem(string Name, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record SlmpCpuTargetItem(SlmpModuleIoTarget Value, string DisplayName);

/// <summary>共通設定（仕様 §24：PLC接続設定・データ保存・アプリ設定）。保存で呼び出し元へ反映する。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(PlcSettings plc, DataSettings data, int initialTab)
    {
        _selectedTab = initialTab;
        _mode = plc.Mode;
        var connection = plc.Clone();
        if (connection.Mode == PlcMode.Dummy)
            connection.Mode = connection.Profile.StartsWith("keyence:", StringComparison.Ordinal) ? PlcMode.HostLink : PlcMode.Slmp;
        _connections[connection.Mode] = connection;
        LoadConnection(connection);
        _csvFolder = data.CsvFolder;
        _confirmParamChange = data.ConfirmParamChangeWhileRunning;
        _trendMinutesText = data.TrendMinutes.ToString();
    }

    public IReadOnlyList<ProfileItem> Profiles => _profiles;
    private IReadOnlyList<ProfileItem> _profiles = [];
    private readonly Dictionary<PlcMode, PlcSettings> _connections = [];
    private bool _loadingConnection;
    public int[] CommCycleOptions { get; } = [50, 100, 200, 500, 1000];
    public IReadOnlyList<SlmpCpuTargetItem> SlmpCpuTargets { get; } = new[]
    {
        SlmpModuleIoTarget.OwnStation,
        SlmpModuleIoTarget.MultipleCpu1, SlmpModuleIoTarget.MultipleCpu2,
        SlmpModuleIoTarget.MultipleCpu3, SlmpModuleIoTarget.MultipleCpu4,
        SlmpModuleIoTarget.ControlSystemCpu, SlmpModuleIoTarget.StandbySystemCpu,
        SlmpModuleIoTarget.SystemACpu, SlmpModuleIoTarget.SystemBCpu,
    }.Select(value => new SlmpCpuTargetItem(value, SlmpPlcClient.ModuleIoDisplayName(value))).ToList();
    public bool TrendMinutesValid => int.TryParse(TrendMinutesText, out int minutes)
        && minutes is >= TrendBuffer.MinRetentionMinutes and <= TrendBuffer.MaxRetentionMinutes;
    public string TrendMinutesError => TrendMinutesValid ? "" : $"{TrendBuffer.MinRetentionMinutes}～{TrendBuffer.MaxRetentionMinutes} 分の整数で入力してください。";

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(UseRealPlc), nameof(ShowSlmpCpuTarget))] private PlcMode _mode;
    [ObservableProperty] private SlmpModuleIoTarget _slmpModuleIo = SlmpModuleIoTarget.OwnStation;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EndpointEditable))] private bool _useGxSimulator;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EndpointEditable))] private bool _useKvSimulator;

    private ProfileItem _selectedProfile = null!;
    public ProfileItem SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            // 機種候補の切替でComboBoxが一時的に選択を解除しても、保存値を失わない。
            if (_loadingConnection || value == null || !_profiles.Contains(value) || !SetProperty(ref _selectedProfile, value)) return;
            if (!CanUseGxSimulator) UseGxSimulator = false;
            if (!CanUseKvSimulator) UseKvSimulator = false;
            OnPropertyChanged(nameof(CanUseGxSimulator));
            OnPropertyChanged(nameof(CanUseKvSimulator));
            OnPropertyChanged(nameof(EndpointEditable));
            ClearTestResult();
        }
    }

    public bool UseRealPlc => Mode != PlcMode.Dummy;
    public bool ShowSlmpCpuTarget => Mode == PlcMode.Slmp;
    partial void OnSlmpModuleIoChanged(SlmpModuleIoTarget value) => ClearTestResult();

    /// <summary>GX Simulator 3 は iQ-R / iQ-L のときだけ選べる</summary>
    public bool CanUseGxSimulator => Mode == PlcMode.Slmp && PlcSettings.SupportsGxSimulator(SelectedProfile.Name);
    public bool CanUseKvSimulator => Mode == PlcMode.HostLink && PlcSettings.SupportsKvSimulator(SelectedProfile.Name);
    public string ConnectionTestHint => Mode == PlcMode.HostLink
        ? "一時的に接続してPLCの応答を確認します（書込みはしません）"
        : "一時的に接続してSD0を読みます（書込みはしません）";

    public bool EndpointEditable => !(UseGxSimulator && CanUseGxSimulator || UseKvSimulator && CanUseKvSimulator);

    partial void OnModeChanged(PlcMode oldValue, PlcMode newValue)
    {
        if (oldValue != PlcMode.Dummy) _connections[oldValue] = Current(oldValue);
        if (newValue != PlcMode.Dummy)
        {
            var next = _connections.GetValueOrDefault(newValue) ?? new PlcSettings
            {
                Mode = newValue,
                Profile = newValue == PlcMode.HostLink ? "keyence:kv-8000" : "melsec:iq-r",
                Port = newValue == PlcMode.HostLink ? 8501 : 1025,
            };
            next.TimeoutMs = TimeoutMs;
            next.CommCycleMs = CommCycleMs;
            next.SlmpModuleIo = SlmpModuleIo;
            LoadConnection(next);
        }
        OnPropertyChanged(nameof(CanUseGxSimulator));
        OnPropertyChanged(nameof(CanUseKvSimulator));
        OnPropertyChanged(nameof(EndpointEditable));
        OnPropertyChanged(nameof(ConnectionTestHint));
        ClearTestResult();
    }

    private void LoadConnection(PlcSettings plc)
    {
        _loadingConnection = true;
        try
        {
            _profiles = (plc.Mode == PlcMode.HostLink ? HostLinkPlcClient.Profiles() : SlmpPlcClient.Profiles())
                .Select(p => new ProfileItem(p.Name, p.DisplayName)).ToList();
            var selected = _profiles.FirstOrDefault(p => p.Name == plc.Profile) ?? _profiles.First();
            OnPropertyChanged(nameof(Profiles));
            _selectedProfile = selected;
            UseGxSimulator = plc.IsGxSimulator;
            UseKvSimulator = plc.IsKvSimulator;
            _realEndpoint = (plc.Host, plc.Port, plc.Udp);
            var endpoint = plc.Effective();
            Host = endpoint.Host;
            Port = endpoint.Port;
            UseUdp = endpoint.Udp;
            TimeoutMs = plc.TimeoutMs;
            CommCycleMs = plc.CommCycleMs;
            SlmpModuleIo = plc.SlmpModuleIo;
        }
        finally { _loadingConnection = false; }
        OnPropertyChanged(nameof(SelectedProfile));
    }

    // シミュレータ選択中は固定値を表示し、解除すると実機用の接続先に戻す。
    private (string Host, int Port, bool Udp) _realEndpoint;

    partial void OnUseGxSimulatorChanged(bool value)
    {
        if (_loadingConnection) return;
        if (value && CanUseGxSimulator)
        {
            _realEndpoint = (Host, Port, UseUdp);
            ShowGxEndpoint();
        }
        else if (!value)
        {
            (Host, Port, UseUdp) = _realEndpoint;
        }
        ClearTestResult();
    }

    partial void OnUseKvSimulatorChanged(bool value)
    {
        if (_loadingConnection) return;
        if (value && CanUseKvSimulator)
        {
            _realEndpoint = (Host, Port, UseUdp);
            Host = PlcSettings.KvSimulatorHost;
            Port = PlcSettings.KvSimulatorPort;
            UseUdp = false;
        }
        else if (!value)
        {
            (Host, Port, UseUdp) = _realEndpoint;
        }
        ClearTestResult();
    }

    private void ShowGxEndpoint()
    {
        Host = PlcSettings.GxSimulatorHost;
        Port = PlcSettings.GxSimulatorPort;
        UseUdp = false;
    }

    [ObservableProperty] private string _host = "";
    [ObservableProperty] private int _port;
    [ObservableProperty] private bool _useUdp;
    [ObservableProperty] private int _timeoutMs;
    [ObservableProperty] private int _commCycleMs;
    [ObservableProperty] private string _csvFolder;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TrendMinutesValid), nameof(TrendMinutesError))]
    private string _trendMinutesText;
    [ObservableProperty] private bool _confirmParamChange;
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _testOk;
    [ObservableProperty] private bool _testing;

    public bool Saved { get; private set; }
    public event Action? CloseRequested;

    private void ClearTestResult() { TestResult = ""; TestOk = false; }

    private PlcSettings Current(PlcMode? mode = null) => new()
    {
        Mode = mode ?? Mode,
        Profile = SelectedProfile.Name,
        Host = (UseGxSimulator || UseKvSimulator ? _realEndpoint.Host : Host).Trim(),
        Port = UseGxSimulator || UseKvSimulator ? _realEndpoint.Port : Port,
        Udp = UseGxSimulator || UseKvSimulator ? _realEndpoint.Udp : UseUdp,
        TimeoutMs = Math.Max(1, TimeoutMs),
        CommCycleMs = Math.Max(20, CommCycleMs),
        SlmpModuleIo = SlmpModuleIo,
        UseGxSimulator = UseGxSimulator && (mode ?? Mode) == PlcMode.Slmp && PlcSettings.SupportsGxSimulator(SelectedProfile.Name),
        UseKvSimulator = UseKvSimulator && (mode ?? Mode) == PlcMode.HostLink && PlcSettings.SupportsKvSimulator(SelectedProfile.Name),
    };

    /// <summary>入力中の設定で一時的に接続し、応答を確認する。PLCへは書き込まない。</summary>
    [RelayCommand]
    private async Task TestConnection()
    {
        if (!UseRealPlc) return;
        Testing = true;
        TestResult = $"{Host}:{Port} に接続しています…";
        try
        {
            var settings = Current();
            var r = settings.Mode == PlcMode.HostLink
                ? await HostLinkPlcClient.TestConnectionAsync(settings)
                : await SlmpPlcClient.TestConnectionAsync(settings);
            TestOk = r.Ok;
            TestResult = r.Message;
        }
        finally
        {
            Testing = false;
        }
    }

    [RelayCommand]
    private void BrowseCsvFolder()
    {
        var dlg = new OpenFolderDialog { Title = "CSVの保存先フォルダ", InitialDirectory = Directory.Exists(CsvFolder) ? CsvFolder : "" };
        if (dlg.ShowDialog() == true) CsvFolder = dlg.FolderName;
    }

    [RelayCommand]
    private void Save()
    {
        if (!TrendMinutesValid) return;
        // 押した時点の値を確定する（ウィンドウを閉じる際のバインディング解除で ComboBox の選択が変わることがあるため）
        _result = Current();
        _resultCsv = CsvFolder.Trim();
        _resultConfirm = ConfirmParamChange;
        _resultTrendMinutes = int.Parse(TrendMinutesText);
        Saved = true;
        CloseRequested?.Invoke();
    }

    private PlcSettings? _result;
    private string? _resultCsv;
    private bool _resultConfirm;
    private int _resultTrendMinutes;

    public void ApplyTo(PlcSettings plc, DataSettings data)
    {
        var c = _result ?? Current();
        plc.Mode = c.Mode;
        plc.Profile = c.Profile;
        plc.Host = c.Host;
        plc.Port = c.Port;
        plc.Udp = c.Udp;
        plc.TimeoutMs = c.TimeoutMs;
        plc.CommCycleMs = c.CommCycleMs;
        plc.SlmpModuleIo = c.SlmpModuleIo;
        plc.UseGxSimulator = c.UseGxSimulator;
        plc.UseKvSimulator = c.UseKvSimulator;
        data.CsvFolder = _resultCsv ?? CsvFolder.Trim();
        data.ConfirmParamChangeWhileRunning = _result != null ? _resultConfirm : ConfirmParamChange;
        data.TrendMinutes = _result != null ? _resultTrendMinutes : int.Parse(TrendMinutesText);
    }
}
