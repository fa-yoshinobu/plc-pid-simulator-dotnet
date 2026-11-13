using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PidSimulator.App.Views;
using PidSimulator.Core;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;

namespace PidSimulator.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private DateTime _toastUntil;
    private int _tick;
    private CancellationTokenSource? _startAllCts;
    private readonly IUserInteraction _interaction;
    private readonly ProjectWorkflow _projects;
    private readonly TargetRegistrationWorkflow _registration;
    private readonly PlcConnectionController _connection;

    public MainViewModel(SimulationEngine engine, DummyPlc dummy, string projectName,
        IUserInteraction? interaction = null, Func<PlcSettings, IPlcClient>? clientFactory = null, bool startUiTimer = true)
    {
        Engine = engine;
        Dummy = dummy;
        _interaction = interaction ?? new WpfUserInteraction();
        _projects = new ProjectWorkflow(this, _interaction);
        _registration = new TargetRegistrationWorkflow(this, _interaction);
        _connection = new PlcConnectionController(engine, dummy, _interaction, CancelStartAll, clientFactory);
        _projectName = projectName;
        foreach (var t in engine.Targets) Targets.Add(new TargetViewModel(this, t));
        foreach (var e in engine.Log.Snapshot().Reverse()) Events.Add(e);
        engine.Log.Added += e => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Events.Insert(0, e);
            if (Events.Count > 2000) Events.RemoveAt(Events.Count - 1);
        });

        Overview = new OverviewViewModel(this);
        _currentPage = Overview;
        Targets.CollectionChanged += (_, _) => UpdateTargetNavigation();
        _timer.Tick += (_, _) => OnTick();
        if (startUiTimer) _timer.Start();
        UpdateSummary();
    }

    public SimulationEngine Engine { get; }
    /// <summary>PLCなしで動かすときのダミーPLC（PLC側PIDの模擬を含む）。常に保持し、通信方式で切り替える。</summary>
    public DummyPlc Dummy { get; }
    public bool IsDummy => Engine.Plc is DummyPlc;
    public bool CanConnect => Engine.Plc is IConnectablePlcClient && Engine.Plc.Status.State == PlcConnectionState.Disconnected;
    public bool CanDisconnect => Engine.Plc is IConnectablePlcClient && Engine.Plc.Status.State != PlcConnectionState.Disconnected;
    public PlcSettings PlcSettings => _connection.Settings;
    public DataSettings DataSettings { get; internal set; } = new();
    public ObservableCollection<TargetViewModel> Targets { get; } = [];
    public ObservableCollection<EventEntry> Events { get; } = [];
    public OverviewViewModel Overview { get; }

    [ObservableProperty] private string _plcStatusText = "";
    [ObservableProperty] private string _plcStateKind = "ok";

    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private int _stoppedCount;
    [ObservableProperty] private int _forceCount;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ResetAllAlarmsCommand))] private int _alarmCount;
    [ObservableProperty] private bool _hasTargets;
    [ObservableProperty] private bool _hasForce;
    [ObservableProperty] private string _forceStripText = "";
    [ObservableProperty] private string? _toastText;
    [ObservableProperty] private bool _hasToast;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(WindowTitle), nameof(ProjectLabel))] private string _projectName;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProjectLabel))] private string? _projectPath;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WindowTitle), nameof(ProjectLabel))] private bool _isDirty;

    public string WindowTitle => $"PID Process Simulator - {ProjectName}{(IsDirty ? " *" : "")}";
    public string ProjectLabel => (ProjectPath != null ? Path.GetFileName(ProjectPath) : $"{ProjectName}（未保存）") + (IsDirty ? " *" : "");

    // ---- 周期更新 ----

    private void OnTick()
    {
        _tick++;
        bool spark = _tick % 5 == 1;
        foreach (var t in Targets) t.Refresh(spark);
        (CurrentPage as DetailViewModel)?.Refresh();
        UpdateSummary();
        if (HasToast && DateTime.Now > _toastUntil) HasToast = false;
    }

    internal void UpdateSummary()
    {
        TotalCount = Targets.Count;
        HasTargets = TotalCount > 0;
        RunningCount = Targets.Count(t => t.IsRunning);
        StoppedCount = TotalCount - RunningCount;
        AlarmCount = Targets.Count(t => t.HasAlarm);
        var forced = Targets.Where(t => t.HasForce).ToList();
        ForceCount = forced.Count;
        HasForce = forced.Count > 0;
        ForceStripText = HasForce
            ? $"{forced.Count} 件の制御対象で強制値を使用しています：" + string.Join("　｜　", forced.Select(DescribeForces))
            : "";
        UpdatePlcStatus();
    }

    private void UpdatePlcStatus()
    {
        if (Engine.Failure is { } failure)
        {
            PlcStateKind = "alarm";
            PlcStatusText = $"演算スレッド停止：{failure.Message}（保存してアプリを再起動してください）";
            return;
        }
        ConnectPlcCommand.NotifyCanExecuteChanged();
        DisconnectPlcCommand.NotifyCanExecuteChanged();
        var s = Engine.Plc.Status;
        if (Engine.Plc is DummyPlc)
        {
            PlcStateKind = "dummy";
            PlcStatusText = "ダミーPLCで動作中（実機には接続していません）";
            return;
        }
        (PlcStateKind, PlcStatusText) = s.State switch
        {
            PlcConnectionState.Disconnected => ("idle", $"{s.Endpoint}　未接続（「接続」で通信開始）"),
            PlcConnectionState.Connected => ("ok", $"{s.Endpoint}　接続中　通信 {s.LastCycleMs:0} ms"),
            PlcConnectionState.Connecting => ("connecting", $"{s.Endpoint}　接続しています…"),
            _ => ("alarm", $"{s.Endpoint}　{s.LastError ?? "切断"}　（自動で再接続します）"),
        };
    }

    private static string DescribeForces(TargetViewModel t)
    {
        var parts = Enum.GetValues<ForceKey>()
            .Select(k => (k, f: t.Model.GetForce(k)))
            .Where(x => x.f.On)
            .Select(x => $"{Labels.Of(x.k)} {x.f.Value:0.0}");
        return $"{t.Name.Split(' ')[0]} {string.Join("、", parts)}";
    }

    public void Notify(string message)
    {
        ToastText = message;
        HasToast = true;
        _toastUntil = DateTime.Now.AddSeconds(3.5);
    }

    public void MarkDirty() => IsDirty = true;

    public bool ConfirmParamChange => DataSettings.ConfirmParamChangeWhileRunning;

    // ---- 画面遷移 ----

    public void Open(TargetViewModel t)
    {
        if (!Targets.Contains(t)) return;
        var previous = CurrentPage as DetailViewModel;
        int selectedTab = previous?.SelectedTabIndex ?? 0;
        previous?.Detach();
        CurrentPage = new DetailViewModel(this, t) { SelectedTabIndex = selectedTab };
    }

    private int CurrentTargetIndex => CurrentPage is DetailViewModel detail ? Targets.IndexOf(detail.Target) : -1;
    public string TargetPositionText => CurrentTargetIndex is var index && index >= 0
        ? $"{index + 1} / {Targets.Count}" : "";

    private bool CanPreviousTarget() => CurrentTargetIndex > 0;
    private bool CanNextTarget() => CurrentTargetIndex >= 0 && CurrentTargetIndex < Targets.Count - 1;

    [RelayCommand(CanExecute = nameof(CanPreviousTarget))]
    private void PreviousTarget() => MoveTarget(-1);

    [RelayCommand(CanExecute = nameof(CanNextTarget))]
    private void NextTarget() => MoveTarget(1);

    private void MoveTarget(int offset)
    {
        int index = CurrentTargetIndex;
        if (index >= 0 && index + offset >= 0 && index + offset < Targets.Count)
            Open(Targets[index + offset]);
    }

    partial void OnCurrentPageChanged(object value) => UpdateTargetNavigation();

    private void UpdateTargetNavigation()
    {
        OnPropertyChanged(nameof(TargetPositionText));
        PreviousTargetCommand.NotifyCanExecuteChanged();
        NextTargetCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Back()
    {
        (CurrentPage as DetailViewModel)?.Detach();
        CurrentPage = Overview;
    }

    // ---- 一括操作（§21） ----

    /// <summary>すべてのシミュレーションを停止する。</summary>
    [RelayCommand]
    private void StopAll()
    {
        _startAllCts?.Cancel();
        int n = Engine.StopAll("全制御停止");
        foreach (var t in Targets) t.Refresh(false);
        Notify(n > 0 ? $"全制御停止：{n} 件の演算とPV書込みを停止しました" : "制御中の対象はありません");
    }

    [RelayCommand]
    private async Task StartAll()
    {
        if (Engine.Failure != null)
        {
            _interaction.Error("演算スレッド停止", "プロジェクトを保存し、アプリを再起動してください。");
            return;
        }
        var candidates = Targets.Where(t => t.IsStopped).ToList();
        if (candidates.Count == 0) { Notify("停止中の制御対象はありません"); return; }
        var blockers = candidates.ToDictionary(t => t, t => TargetBlockers(t.Model));
        var ok = candidates.Where(t => !t.HasAlarm && blockers[t].Count == 0).ToList();
        var ng = candidates.Where(t => t.HasAlarm).ToList();

        var items = ok.Select(t => new CheckResult(CheckLevel.Ok, $"{t.Name}　PV → {t.PvAddress}"))
            .Concat(ng.Select(t => new CheckResult(CheckLevel.Warn, $"開始しない（異常中）: {t.Name}")))
            .Concat(candidates.SelectMany(t => blockers[t].Select(c => new CheckResult(CheckLevel.Block, $"{t.Name}: {c.Message}"))));
        if (ok.Count == 0) { _interaction.Error("全制御開始", "開始できる制御対象がありません。", items); return; }
        if (!_interaction.Confirm("全制御開始", "次の制御対象の演算とPV書込みを開始します。", $"{ok.Count} 件を開始", items: items)) return;

        using var cts = new CancellationTokenSource();
        _startAllCts = cts;
        var plc = Engine.Plc;
        try
        {
            if (plc is IConnectablePlcClient client && client.Status.State != PlcConnectionState.Connected)
            {
                Notify("PLCへ接続しています。接続完了後に制御を開始します");
                var result = await client.ConnectAndWaitAsync(cts.Token);
                UpdatePlcStatus();
                if (!result.Ok)
                {
                    _interaction.Error("全制御を開始できません", result.Message);
                    return;
                }
            }
            cts.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(plc, Engine.Plc)) return;
            if (plc.Status.State != PlcConnectionState.Connected)
            {
                _interaction.Error("全制御を開始できません", "PLCに接続できませんでした。");
                return;
            }
            int started = ok.Count(t => t.Model.Start(Engine.Log, out _));
            foreach (var t in Targets) t.Refresh(false);
            Notify($"{started} 件の制御を開始しました");
        }
        catch (OperationCanceledException)
        {
            Notify("全制御開始をキャンセルしました");
        }
        finally
        {
            _startAllCts = null;
        }
    }

    internal IReadOnlyList<CheckResult> TargetBlockers(ControlTarget target) =>
        RegistrationValidator.Check(target.ToConfig(), Engine.Targets.Where(t => t != target), PlcSettings)
            .Where(c => c.Level == CheckLevel.Block).ToList();

    private bool CanResetAllAlarms() => AlarmCount > 0;

    [RelayCommand(CanExecute = nameof(CanResetAllAlarms))]
    private void ResetAllAlarms()
    {
        int n = Engine.ResetAllAlarms();
        foreach (var t in Targets) t.Refresh(false);
        UpdateSummary();
        Notify(n > 0 ? $"{n} 件の異常リセットを要求しました。通信異常は応答確認後に解除します" : "異常中の対象はありません");
    }

    [RelayCommand]
    private void ReleaseAll()
    {
        int n = Engine.ReleaseAllForces();
        foreach (var t in Targets) t.Refresh(false);
        Notify(n > 0 ? $"{n} 件のFORCEを解除しました" : "FORCE中の項目はありません");
    }

    // ---- 登録・編集・複製・削除（§5・§22） ----
    [RelayCommand]
    private void Register() => _registration.Register();
    public void Edit(TargetViewModel target) => _registration.Edit(target);
    public void Duplicate(TargetViewModel target) => _registration.Duplicate(target);
    public void Delete(TargetViewModel target) => _registration.Delete(target);
    internal void BackToOverview() => Back();

    // ---- プロジェクト（§13） ----

    [RelayCommand]
    private void NewProject() => _projects.New();
    [RelayCommand]
    private void OpenProject() => _projects.Open();
    [RelayCommand]
    private void OpenDemo() => _projects.OpenDemo();
    [RelayCommand]
    private void SaveProject() => _projects.Save();
    [RelayCommand]
    private void SaveProjectAs() => _projects.SaveAs();
    public string? SaveRecovery() => _projects.SaveRecovery();
    public bool ConfirmLeave(string action) => _projects.ConfirmLeave(action);
    internal void CancelStartAll() => _startAllCts?.Cancel();

    // ---- 共通設定・その他 ----

    [RelayCommand]
    private void OpenSettings(string? tab)
    {
        var vm = new SettingsViewModel(PlcSettings, DataSettings, int.TryParse(tab, out int i) ? i : 0);
        if (!_interaction.EditSettings(vm)) return;
        var next = PlcSettings.Clone();
        var nextData = new DataSettings();
        vm.ApplyTo(next, nextData);
        if (!ApplyPlcSettings(next, force: false)) return;
        DataSettings = nextData;
        Engine.SetTrendRetention(DataSettings.TrendMinutes);
        MarkDirty();
        Notify("共通設定を保存しました");
    }

    internal bool ApplyPlcSettings(PlcSettings next, bool force)
    {
        if (!_connection.Apply(next, force)) return false;
        foreach (var target in Targets) target.Refresh(false);
        OnPropertyChanged(nameof(IsDummy));
        UpdatePlcStatus();
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void ConnectPlc()
    {
        if (!_connection.Connect()) return;
        UpdatePlcStatus();
        Notify("PLCへの接続を開始しました");
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectPlc()
    {
        await _connection.DisconnectAsync();
        foreach (var target in Targets) target.Refresh(false);
        UpdatePlcStatus();
        Notify("通信を切断しました");
    }

    public bool CheckPlcConnected()
    {
        if (Engine.Plc.Status.State == PlcConnectionState.Connected) return true;
        _interaction.Info("PLC未接続", "「接続」を押し、接続完了後に制御を開始してください。");
        return false;
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (CurrentPage is DetailViewModel d) d.ExportCsvCommand.Execute(null);
        else _interaction.Info("試験データのCSV出力", "一覧から制御対象を開き、詳細画面で実行してください。");
    }

    [RelayCommand]
    private void Exit() => Application.Current.MainWindow?.Close();

    [RelayCommand]
    private void About() => new AboutWindow { Owner = Application.Current.MainWindow }.ShowDialog();
}

public sealed class OverviewViewModel(MainViewModel main)
{
    public MainViewModel Main { get; } = main;
}
