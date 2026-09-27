using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Microsoft.Win32;
using PidSimulator.Core.Project;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PidSimulator.Core;

namespace PidSimulator.App.ViewModels;

public sealed partial class DetailViewModel : ObservableObject
{
    public DetailViewModel(MainViewModel main, TargetViewModel target)
    {
        Main = main;
        Target = target;
        var info = target.Model.Info;
        var m = target.Model;
        ForceRows =
        [
            new(target, main, ForceKey.Mv, "MV FORCE", "PLCから受信したMVを無視して固定値を使う", m.MvRange.Unit),
            new(target, main, ForceKey.Pv, "PV FORCE", $"{m.PvAddress} へ固定値を書き込む", m.PvRange.Unit),
            new(target, main, ForceKey.Sp, "SP FORCE", m.UseSp ? $"{m.SpAddress} の読込値を無視する" : "内部SPを固定", m.SpRange.Unit),
            new(target, main, ForceKey.Disturbance, "外乱 FORCE", info.DistLabel, info.DistUnit),
            new(target, main, ForceKey.ModelInput, "モデル固有入力 FORCE", info.InputLabel, info.InputUnit),
        ];
        Events = new ListCollectionView(main.Events)
        {
            Filter = o => o is EventEntry e && e.TargetId == m.Id && (EventFilter == "すべて" || e.Category == EventFilter),
        };
        var s = m.Snapshot();
        _distValue = s.DisturbanceValue;
        _previewMv = s.PreviewMv;
        Refresh();
    }

    public MainViewModel Main { get; }
    public TargetViewModel Target { get; }
    public ObservableCollection<ForceRowViewModel> ForceRows { get; }
    public ListCollectionView Events { get; }
    public string[] Categories { get; } = ["すべて", "運転", "FORCE", "外乱", "通信", "異常", "リセット", "登録"];
    public string DistLabel => Target.Model.Info.DistLabel;
    public string DistUnit => Target.Model.Info.DistUnit;
    public double DistMax => Target.Model.Info.DistMax;

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private string _eventFilter = "すべて";
    [ObservableProperty] private double _distValue;
    private double _previewMv;
    [ObservableProperty] private string _distStatus = "";
    [ObservableProperty] private bool _distForced;
    [ObservableProperty] private bool _distDelayed;
    [ObservableProperty] private string _distDelayText = "10";
    [ObservableProperty] private bool _distHasDuration;
    [ObservableProperty] private string _distDurationText = "60";
    [ObservableProperty] private string _distScheduleStatus = "予約なし";
    [ObservableProperty] private string _distScheduleError = "";
    [ObservableProperty] private bool _distCanCancel;

    partial void OnEventFilterChanged(string value) => Events.Refresh();
    partial void OnDistValueChanged(double value) => Target.Model.SetDisturbanceValue(value);

    public bool IsRelayPreview => Target.Model.MvOnOff;
    public double PreviewMvMin => Target.Model.MvRange.EngMin;
    public double PreviewMvMax => Target.Model.MvRange.EngMax;
    public double PreviewMvSmallChange => (PreviewMvMax - PreviewMvMin) / 200;
    public double PreviewMvLargeChange => (PreviewMvMax - PreviewMvMin) / 20;
    public double PreviewMv
    {
        get => _previewMv;
        set
        {
            Target.Model.SetPreviewMv(value);
            UpdatePreviewMv(Target.Model.Snapshot().PreviewMv);
        }
    }

    public bool PreviewOn
    {
        get => PreviewMv >= 50;
        set => PreviewMv = value ? 100 : 0;
    }

    public string PreviewMvText => IsRelayPreview
        ? (PreviewOn ? "ON（100%）" : "OFF（0%）")
        : $"{PreviewMv:F1} {Target.Model.MvRange.Unit}";

    private void UpdatePreviewMv(double value)
    {
        if (!SetProperty(ref _previewMv, value, nameof(PreviewMv))) return;
        OnPropertyChanged(nameof(PreviewOn));
        OnPropertyChanged(nameof(PreviewMvText));
    }

    public void Refresh()
    {
        var s = Target.Model.Snapshot();
        foreach (var row in ForceRows) row.Refresh(s);
        DistForced = s.ActiveForces.Contains(ForceKey.Disturbance);
        DistStatus = DistForced ? $"FORCE適用中：{s.ActualDisturbance:G} {DistUnit}" : s.DisturbanceOn
            ? $"投入中　{Main.Engine.ToTime(s.DisturbanceSince):HH:mm:ss} から"
            : "現在: 外乱なし";
        var schedule = Target.Model.GetDisturbanceSchedule();
        DistCanCancel = schedule.State is DisturbanceScheduleState.Waiting or DisturbanceScheduleState.Active;
        DistScheduleStatus = schedule.State switch
        {
            DisturbanceScheduleState.Waiting => s.RunState == RunState.Stopped
                ? $"予約済み：運転・プレビュー開始から {schedule.SecondsUntilStart:0.0} 秒後"
                : schedule.SecondsUntilStart <= 1e-9 ? "次の演算で投入" : $"投入まで {schedule.SecondsUntilStart:0.0} 秒",
            DisturbanceScheduleState.Active => (DistForced ? "予約実行中（FORCE優先）：" : "投入中：") + $"{schedule.Value:G} {DistUnit}"
                + (schedule.SecondsRemaining is { } seconds
                    ? seconds <= 1e-9 ? "（次の演算で解除）" : $"（あと {seconds:0.0} 秒）"
                    : "（解除まで継続）"),
            DisturbanceScheduleState.Completed => "終了：予約外乱を解除しました",
            DisturbanceScheduleState.Cancelled => "取消済み",
            _ => "予約なし",
        };
        UpdatePreviewMv(s.PreviewMv);
    }

    public void Detach() => Events.DetachFromSourceCollection();

    /// <summary>試験データのCSV出力（仕様 §17）。保持している全履歴を書き出す。</summary>
    [RelayCommand]
    private void ExportCsv()
    {
        var t = Target.Model;
        var samples = new List<TrendSample>();
        lock (t.Sync) t.Trend.CopyRange(double.MinValue, double.MaxValue, samples);
        if (samples.Count == 0)
        {
            Dialogs.Info("試験データのCSV出力", "記録されたデータがありません。");
            return;
        }

        string tag = string.Concat(t.Name.Split(' ')[0].Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        string folder = Main.DataSettings.CsvFolder;
        var dlg = new SaveFileDialog
        {
            Title = "試験データをCSVで保存",
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"{tag}_{Main.Engine.Now:yyyyMMdd_HHmmss}.csv",
            InitialDirectory = Directory.Exists(folder) ? folder : "",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using (var w = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true)))
                TrendCsv.Write(w, t, samples, Main.Engine.ToTime);
            Main.Engine.Log.Add(t, "運転", $"試験データをCSV出力（{samples.Count} 点）: {Path.GetFileName(dlg.FileName)}");
            Main.Notify($"{samples.Count} 点を保存しました：{Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error("CSVを保存できません", $"{dlg.FileName}\n\n{ex.Message}");
        }
    }
    [RelayCommand]
    private void DistOn()
    {
        Target.Model.SetDisturbance(true, Main.Engine.T, Main.Engine.Log);
        Target.Refresh(false);
        Refresh();
    }

    [RelayCommand]
    private void DistOff()
    {
        Target.Model.SetDisturbance(false, Main.Engine.T, Main.Engine.Log);
        Target.Refresh(false);
        Refresh();
    }

    [RelayCommand]
    private void ScheduleDist()
    {
        DistScheduleError = "";
        double delay = 0;
        double? duration = null;
        if (DistDelayed && (!double.TryParse(DistDelayText, out delay) || !double.IsFinite(delay) || delay < 0 || delay > 86400))
        {
            DistScheduleError = "開始までの時間は 0～86400 秒で入力してください。";
            return;
        }
        if (DistHasDuration)
        {
            if (!double.TryParse(DistDurationText, out double seconds) || !double.IsFinite(seconds) || seconds < 0.1 || seconds > 86400)
            {
                DistScheduleError = "継続時間は 0.1～86400 秒で入力してください。";
                return;
            }
            duration = seconds;
        }
        if (!Target.Model.ScheduleDisturbance(DistValue, delay, duration, Main.Engine.Log, out string? error))
        {
            DistScheduleError = error ?? "設定を確認してください。";
            return;
        }
        Target.Refresh(false);
        Refresh();
    }

    [RelayCommand]
    private void CancelDistSchedule()
    {
        Target.Model.CancelDisturbanceSchedule(Main.Engine.Log);
        DistScheduleError = "";
        Target.Refresh(false);
        Refresh();
    }

    [RelayCommand]
    private void ReleaseForces()
    {
        Target.Model.ReleaseForces(Main.Engine.Log);
        Target.Refresh(false);
        Refresh();
    }
}

public sealed partial class ForceRowViewModel : ObservableObject
{
    private readonly TargetViewModel _target;
    private readonly MainViewModel _main;

    public ForceRowViewModel(TargetViewModel target, MainViewModel main, ForceKey key, string label, string description, string unit)
    {
        _target = target;
        _main = main;
        Key = key;
        Label = label;
        Description = description;
        Unit = unit;
        var (on, value) = target.Model.GetForce(key);
        _isOn = on;
        _value = value;
    }

    public ForceKey Key { get; }
    public string Label { get; }
    public string Description { get; }
    public string Unit { get; }

    [ObservableProperty] private double _value;
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private string _actualText = "";

    partial void OnValueChanged(double value)
    {
        if (IsOn) _target.Model.SetForce(Key, true, value, _main.Engine.Log);
    }

    public void Refresh(TargetSnapshot s)
    {
        IsOn = s.ActiveForces.Contains(Key);
        int dec = _target.Model.Info.Decimals;
        ActualText = Key switch
        {
            ForceKey.Mv => s.Mv.ToString("F1"),
            ForceKey.Pv => s.Pv.ToString("F" + dec),
            ForceKey.Sp => s.Sp.ToString("F" + dec),
            ForceKey.Disturbance => s.ActualDisturbance.ToString("F1"),
            _ => "—",
        };
    }

    [RelayCommand]
    private void Toggle()
    {
        _target.Model.SetForce(Key, !IsOn, Value, _main.Engine.Log);
        IsOn = !IsOn;
        _target.Refresh(false);
    }
}
