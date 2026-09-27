using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PidSimulator.App.Controls;
using PidSimulator.Core;
using PidSimulator.Core.Models;

namespace PidSimulator.App.ViewModels;

/// <summary>パラメータタブの1行。入力欄を離れた時点で確定し、失敗したら元の値に戻す。</summary>
public sealed partial class ParamEditViewModel : ObservableObject
{
    private readonly TargetViewModel _owner;
    private double _value;

    public ParamEditViewModel(TargetViewModel owner, ParamDef def)
    {
        _owner = owner;
        Def = def;
        _value = owner.Model.GetParam(def.Key);
    }

    public ParamDef Def { get; }
    public string Label => Def.Label;
    public string Unit => Def.Unit;
    public string HelpText => $"{Def.Description}\n入力範囲: {Def.Min:G} ～ {Def.Max:G} {Def.Unit}".Trim();
    public bool StopOnly => Def.StopOnly;
    public bool Warn => Def.Warn;
    public bool IsChoice => Def.Choices.Count > 0;
    public IReadOnlyList<string> Choices => Def.Choices;

    [ObservableProperty] private bool _isEditable = true;

    public string ValueText
    {
        get => _value.ToString("G");
        set
        {
            if (double.TryParse(value, out double v) && _owner.CommitParam(this, v)) _value = v;
            OnPropertyChanged();
        }
    }

    public int ChoiceIndex
    {
        get => (int)_value;
        set
        {
            if (value >= 0 && _owner.CommitParam(this, value)) _value = value;
            OnPropertyChanged();
        }
    }

    public void Reload()
    {
        _value = _owner.Model.GetParam(Def.Key);
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(ChoiceIndex));
    }
}

/// <summary>制御対象1件の表示用ラッパー。<see cref="Refresh"/> でエンジンのスナップショットを取り込む。</summary>
public sealed partial class TargetViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _isPreview;

    public TargetViewModel(MainViewModel main, ControlTarget model)
    {
        _main = main;
        Model = model;
        var info = model.Info;
        Sp = new SignalViewModel("sp", "SP　設定値", model.SpRange, info.Decimals)
        { Source = model.UseSp ? $"読込 ← {model.SpAddress}" : "内部設定" };
        Pv = new SignalViewModel("pv", "PV　プロセス値", model.PvRange, info.Decimals) { Source = $"書込 → {model.PvAddress}" };
        Mv = new SignalViewModel("mv", "MV　操作量", model.MvRange, 1);

        var m = Regex.Match(model.Name, @"^([A-Z]+)-?(\d+)");
        IsaLetters = m.Success ? m.Groups[1].Value : info.Isa;
        IsaNumber = m.Success ? m.Groups[2].Value : "—";

        Params = info.Params.Select(p => new ParamEditViewModel(this, p)).ToList();
        Refresh(true);
    }

    public ControlTarget Model { get; }

    public string Name => Model.Name;
    public string ModelName => Model.Info.Name;
    public string Subtitle => string.IsNullOrEmpty(Model.Description) ? ModelName : $"{ModelName}　{Model.Description}";
    public string DetailSubtitle => $"{ModelName}モデル　MV: {Model.Info.MvLabel}　PV: {Model.Info.PvLabel} [{Model.PvRange.Unit}]";
    public string IsaLetters { get; }
    public string IsaNumber { get; }
    public string MvAddress => Model.MvAddress;
    public string PvAddress => Model.PvAddress;
    public string SpAddressText => Model.UseSp ? Model.SpAddress : "（未使用）";
    public string CycleText => $"演算 {SimulationEngine.Dt * 1000:0} ms";
    public string MvRangeText => Model.MvOnOff ? "リレー入力：OFF = 0%、ON = 100%" : Model.MvRange.ToString();
    public string PvRangeText => Model.PvRange.ToString();
    public string SpRangeText => Model.SpRange.ToString();
    public string DataType => $"{(Model.MvOnOff ? "MV: BIT ／ PV・SP: " : "")}{Model.DataType}";
    public string OnCommErrorText => Labels.Of(Model.OnCommError);
    public string RecoverText => Model.Recover == RecoverMode.Auto ? "自動再開" : "手動再開";
    public string InitialPvText => $"{Model.InitialPv:G} {Model.PvRange.Unit}";
    public string PvUnit => Model.PvRange.Unit;
    public string MvUnit => Model.MvRange.Unit;
    public IReadOnlyList<ParamEditViewModel> Params { get; }

    public SignalViewModel Sp { get; }
    public SignalViewModel Pv { get; }
    public SignalViewModel Mv { get; }

    [ObservableProperty] private string _stateKind = "stop";
    [ObservableProperty] private string _stateText = "停止";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isStopped = true;
    [ObservableProperty] private bool _canPreview = true;
    [ObservableProperty] private string? _alarm;
    [ObservableProperty] private bool _hasAlarm;
    [ObservableProperty] private bool _hasForce;
    [ObservableProperty] private string _forceText = "";
    [ObservableProperty] private string _commKind = "ok";
    [ObservableProperty] private string _commText = "";
    [ObservableProperty] private string _elapsedText = "00:00:00";
    [ObservableProperty] private bool _pvWriting;
    [ObservableProperty] private string _pvWriteText = "";
    [ObservableProperty] private bool _distOn;
    [ObservableProperty] private string _demoFaultText = "";
    [ObservableProperty] private bool _isDummy = true;
    [ObservableProperty] private SparkData? _spark;

    partial void OnIsRunningChanged(bool value)
    {
        foreach (var p in Params) p.IsEditable = !(p.StopOnly && value);
    }

    /// <summary>モデル単体プレビュー（仕様 §20）。チェックボックスと双方向バインドする。</summary>
    public bool IsPreview
    {
        get => _isPreview;
        set
        {
            if (value == _isPreview) return;
            Model.SetPreview(value, _main.Engine.Log);
            Refresh(false);
        }
    }

    // ---- 通信異常時・停止時の動作（§7・§14）：通信設定タブで直接変更する ----

    public CommErrorAction OnCommError
    {
        get => Model.OnCommError;
        set => SetBehavior(value, SafeMv, Recover, StopPv, StopPvValue);
    }

    public double SafeMv
    {
        get => Model.SafeMv;
        set => SetBehavior(OnCommError, value, Recover, StopPv, StopPvValue);
    }

    public RecoverMode Recover
    {
        get => Model.Recover;
        set => SetBehavior(OnCommError, SafeMv, value, StopPv, StopPvValue);
    }

    public StopPvMode StopPv
    {
        get => Model.StopPv;
        set => SetBehavior(OnCommError, SafeMv, Recover, value, StopPvValue);
    }

    public double StopPvValue
    {
        get => Model.StopPvValue;
        set => SetBehavior(OnCommError, SafeMv, Recover, StopPv, value);
    }

    private void SetBehavior(CommErrorAction onError, double safeMv, RecoverMode recover, StopPvMode stopPv, double stopPvValue)
    {
        Model.SetCommBehavior(onError, safeMv, recover, stopPv, stopPvValue, _main.Engine.Log);
        _main.MarkDirty();
        OnPropertyChanged(string.Empty);
    }

    public void Refresh(bool updateSpark)
    {
        var s = Model.Snapshot();
        bool alarm = s.Alarm != null || s.Comm != CommStatus.Ok;
        StateKind = alarm ? "alarm" : s.RunState switch { RunState.Running => "run", RunState.Preview => "preview", _ => "stop" };
        StateText = alarm ? "異常" : s.RunState switch { RunState.Running => "制御中", RunState.Preview => "プレビュー", _ => "停止" };
        IsRunning = s.RunState == RunState.Running;
        IsStopped = s.RunState == RunState.Stopped;
        CanPreview = !IsRunning;
        SetProperty(ref _isPreview, s.RunState == RunState.Preview, nameof(IsPreview));

        Alarm = s.Alarm;
        HasAlarm = s.Alarm != null;
        HasForce = s.ActiveForces.Length > 0;
        ForceText = HasForce ? "FORCE " + string.Join("・", s.ActiveForces.Select(k => Labels.Of(k))) : "";

        if (s.RunState == RunState.Preview) { CommKind = "off"; CommText = "PLC切離し"; }
        else { CommKind = s.Comm == CommStatus.Ok ? "ok" : "alarm"; CommText = Labels.Of(s.Comm); }

        Sp.Update(s.Sp, s.SpRaw, s.ActiveForces.Contains(ForceKey.Sp));
        Pv.Update(s.Pv, s.PvRaw, s.ActiveForces.Contains(ForceKey.Pv));
        Mv.Update(s.Mv, s.MvRaw, s.ActiveForces.Contains(ForceKey.Mv));
        Mv.Source = s.RunState == RunState.Preview ? "GUIから入力" : $"読込 ← {Model.MvAddress}";

        ElapsedText = TimeSpan.FromSeconds(s.Elapsed).ToString(@"hh\:mm\:ss");
        PvWriting = s.PvWriting;
        PvWriteText = s.PvWriting ? "● 書込中" : "○ 書込停止";
        DistOn = s.DisturbanceOn;
        IsDummy = _main.IsDummy;
        DemoFaultText = _main.Dummy.IsFaulted(Model.MvAddress) ? "デモ: 通信を復旧" : "デモ: 通信断を発生";

        if (updateSpark) UpdateSpark();
    }

    private void UpdateSpark()
    {
        const double window = 120;
        double now = _main.Engine.T;
        var list = new List<TrendSample>();
        lock (Model.Sync) Model.Trend.CopyRange(now - window, now, list);
        if (list.Count < 2) { Spark = null; return; }

        int step = Math.Max(1, list.Count / 240);
        var picked = list.Where((_, i) => i % step == 0 || i == list.Count - 1).ToList();
        double lo = picked.Min(p => Math.Min(p.Pv, p.Sp)), hi = picked.Max(p => Math.Max(p.Pv, p.Sp));
        double minSpan = (Model.PvRange.EngMax - Model.PvRange.EngMin) * 0.08;
        if (hi - lo < minSpan) { double mid = (hi + lo) / 2; lo = mid - minSpan / 2; hi = mid + minSpan / 2; }
        Spark = new SparkData(picked.Select(p => p.T).ToArray(), picked.Select(p => p.Pv).ToArray(),
            picked.Select(p => p.Sp).ToArray(), lo, hi, now - window, now);
    }

    // ---- パラメータ（§12） ----

    [RelayCommand]
    private void CalculateParams()
    {
        if (!IsStopped) { Dialogs.Info("条件から計算", "制御を停止してから開いてください。"); return; }
        var config = Model.ToConfig();
        var vm = new ParameterWizardViewModel(Model.Kind, config.Params, config);
        var window = new Views.ParameterWizardWindow(vm) { Owner = System.Windows.Application.Current.MainWindow };
        if (window.ShowDialog() != true || vm.Result == null) return;
        if (!Model.ApplyCalculatedParameters(vm.Result.Values, _main.Engine.Log, out var error))
        {
            Dialogs.Error("適用できません", error ?? "");
            return;
        }
        foreach (var row in Params) row.Reload();
        _main.MarkDirty();
        _main.Notify("計算したモデルパラメータを適用しました");
    }

    public bool CommitParam(ParamEditViewModel row, double value)
    {
        double old = Model.GetParam(row.Def.Key);
        if (old == value) return true;
        string unit = row.Unit.Length > 0 ? " " + row.Unit : "";
        if (IsRunning && row.Warn && _main.ConfirmParamChange &&
            !Dialogs.Confirm("運転中のパラメータ変更",
                $"{row.Label} を {old:G} → {value:G}{unit} に変更します。\n\n運転中に変更すると PV が急変し、PLC側のPIDが大きく反応する可能性があります。PV書込先 {PvAddress} には変更直後の値がそのまま書き込まれます。",
                "変更を適用", danger: true))
            return false;
        if (!Model.SetParam(row.Def.Key, value, _main.Engine.Log, out var error))
        {
            Dialogs.Error("変更できません", error ?? "");
            return false;
        }
        _main.MarkDirty();
        _main.Notify($"{row.Label} を {value:G}{unit} に変更しました");
        return true;
    }

    [RelayCommand]
    private void ResetParams()
    {
        if (!IsStopped) { Dialogs.Info("標準値に戻す", "停止してから実行してください。"); return; }
        if (!Dialogs.Confirm("標準値に戻す", $"{Name} のモデルパラメータをすべて標準値に戻します。", "標準値に戻す")) return;
        Model.ResetParams(_main.Engine.Log);
        foreach (var p in Params) p.Reload();
        _main.MarkDirty();
    }

    // ---- 操作 ----

    [RelayCommand]
    private void Open() => _main.Open(this);

    [RelayCommand]
    private void Edit() => _main.Edit(this);

    [RelayCommand]
    private void Duplicate() => _main.Duplicate(this);

    [RelayCommand]
    private void Delete() => _main.Delete(this);

    /// <summary>制御開始前の設定チェック（仕様 §7）</summary>
    [RelayCommand]
    private void Start()
    {
        var blockers = _main.TargetBlockers(Model);
        if (blockers.Count > 0)
        {
            Dialogs.Error("制御を開始できません", $"{Name} の登録内容と通信設定を確認してください。", blockers);
            return;
        }
        if (!_main.CheckPlcConnected()) return;
        var warnings = new List<Core.Project.CheckResult>();
        var dup = _main.Engine.Targets.FirstOrDefault(x => x != Model && string.Equals(x.PvAddress, Model.PvAddress, StringComparison.OrdinalIgnoreCase));
        if (dup != null) warnings.Add(new(Core.Project.CheckLevel.Warn, $"PV書込アドレス {Model.PvAddress} が「{dup.Name}」と重複しています"));
        var forces = Model.Snapshot().ActiveForces;
        if (forces.Length > 0) warnings.Add(new(Core.Project.CheckLevel.Warn, $"FORCE中の項目があります（{string.Join("・", forces.Select(k => Labels.Of(k)))}）"));

        if (warnings.Count > 0 &&
            !Dialogs.Confirm("開始前の確認", $"{Name} の設定に確認が必要な項目があります。", "確認して開始", items: warnings))
            return;

        if (!Model.Start(_main.Engine.Log, out var error))
        {
            Dialogs.Error("制御を開始できません", $"{Name}\n\n{error}");
            return;
        }
        _main.Notify($"{Name} の制御を開始しました。PVを {PvAddress} へ書き込みます");
        Refresh(false);
    }

    [RelayCommand]
    private void Stop()
    {
        if (Model.Stop(_main.Engine.Log)) _main.Notify($"{Name} の制御を停止しました");
        Refresh(false);
    }

    [RelayCommand]
    private void Reset()
    {
        string extra = IsRunning ? "\n\n制御中のため、リセット直後のPVがPLCへ書き込まれます。" : "";
        if (!Dialogs.Confirm("リセット", $"{Name} を初期状態（PV {InitialPvText}）に戻し、経過時間を0にします。{extra}", "リセット")) return;
        Model.Reset(_main.Engine.Log);
        Refresh(true);
    }

    [RelayCommand]
    private void ResetAlarm()
    {
        if (!Model.ResetAlarm(_main.Engine.Log)) return;
        Refresh(false);
        _main.Notify(HasAlarm
            ? "異常リセットを要求しました。通信異常は応答確認後に解除します"
            : "異常をリセットしました");
    }

    [RelayCommand]
    private void ToggleDemoFault()
    {
        bool faulted = _main.Dummy.IsFaulted(Model.MvAddress);
        _main.Dummy.SetFault(Model.MvAddress, !faulted);
        _main.Notify(faulted ? $"デモ: {Model.MvAddress} の応答を戻しました。「異常リセット」で異常を解除できます" : $"デモ: {Model.MvAddress} を応答なしにしました");
        Refresh(false);
    }
}
