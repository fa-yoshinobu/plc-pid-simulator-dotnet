using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PidSimulator.Core;
using PidSimulator.Core.Project;
using PidSimulator.Plc.Slmp;

namespace PidSimulator.App.ViewModels;

public sealed record ProfileItem(string Name, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>共通設定（仕様 §24：PLC接続設定・データ保存・アプリ設定）。保存で呼び出し元へ反映する。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(PlcSettings plc, DataSettings data, int initialTab)
    {
        _selectedTab = initialTab;
        _useSlmp = plc.Mode == PlcMode.Slmp;
        Profiles = SlmpPlcClient.Profiles().Select(p => new ProfileItem(p.Name, p.DisplayName)).ToList();
        _selectedProfile = Profiles.FirstOrDefault(p => p.Name == plc.Profile) ?? Profiles.First();
        _host = plc.Host;
        _port = plc.Port;
        _useUdp = plc.Udp;
        _timeoutMs = plc.TimeoutMs;
        _commCycleMs = plc.CommCycleMs;
        _useGxSimulator = plc.IsGxSimulator;
        _realEndpoint = (plc.Host, plc.Port, plc.Udp);
        if (_useGxSimulator) ShowGxEndpoint();
        _csvFolder = data.CsvFolder;
        _confirmParamChange = data.ConfirmParamChangeWhileRunning;
        _trendMinutesText = data.TrendMinutes.ToString();
    }

    public IReadOnlyList<ProfileItem> Profiles { get; }
    public int[] CommCycleOptions { get; } = [50, 100, 200, 500, 1000];
    public bool TrendMinutesValid => int.TryParse(TrendMinutesText, out int minutes)
        && minutes is >= TrendBuffer.MinRetentionMinutes and <= TrendBuffer.MaxRetentionMinutes;
    public string TrendMinutesError => TrendMinutesValid ? "" : $"{TrendBuffer.MinRetentionMinutes}～{TrendBuffer.MaxRetentionMinutes} 分の整数で入力してください。";

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _useSlmp;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanUseGxSimulator), nameof(EndpointEditable))] private ProfileItem _selectedProfile;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EndpointEditable))] private bool _useGxSimulator;

    /// <summary>GX Simulator 3 は iQ-R / iQ-L のときだけ選べる</summary>
    public bool CanUseGxSimulator => PlcSettings.SupportsGxSimulator(SelectedProfile.Name);

    public bool EndpointEditable => !(UseGxSimulator && CanUseGxSimulator);

    partial void OnSelectedProfileChanged(ProfileItem value)
    {
        if (!PlcSettings.SupportsGxSimulator(value.Name)) UseGxSimulator = false;
    }

    // 実機の接続先。GX Simulator 3 の間は固定値を表示し、チェックを外すとこの値に戻す（保存もこの値）
    private (string Host, int Port, bool Udp) _realEndpoint;

    partial void OnUseGxSimulatorChanged(bool value)
    {
        if (value && CanUseGxSimulator)
        {
            _realEndpoint = (Host, Port, UseUdp);
            ShowGxEndpoint();
        }
        else if (!value)
        {
            (Host, Port, UseUdp) = _realEndpoint;
        }
    }

    private void ShowGxEndpoint()
    {
        Host = PlcSettings.GxSimulatorHost;
        Port = PlcSettings.GxSimulatorPort;
        UseUdp = false;
    }

    [ObservableProperty] private string _host;
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

    private PlcSettings Current() => new()
    {
        Mode = UseSlmp ? PlcMode.Slmp : PlcMode.Dummy,
        Profile = SelectedProfile.Name,
        Host = (EndpointEditable ? Host : _realEndpoint.Host).Trim(),
        Port = EndpointEditable ? Port : _realEndpoint.Port,
        Udp = EndpointEditable ? UseUdp : _realEndpoint.Udp,
        TimeoutMs = Math.Max(1, TimeoutMs),
        CommCycleMs = Math.Max(20, CommCycleMs),
        UseGxSimulator = UseGxSimulator && CanUseGxSimulator,
    };

    /// <summary>入力中の設定で一時的に接続し、SD0 を読んで確認する。PLCへは書き込まない。</summary>
    [RelayCommand]
    private async Task TestConnection()
    {
        Testing = true;
        TestResult = $"{Host}:{Port} に接続しています…";
        try
        {
            var r = await SlmpPlcClient.TestConnectionAsync(Current());
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
        plc.UseGxSimulator = c.UseGxSimulator;
        data.CsvFolder = _resultCsv ?? CsvFolder.Trim();
        data.ConfirmParamChangeWhileRunning = _result != null ? _resultConfirm : ConfirmParamChange;
        data.TrendMinutes = _result != null ? _resultTrendMinutes : int.Parse(TrendMinutesText);
    }
}
