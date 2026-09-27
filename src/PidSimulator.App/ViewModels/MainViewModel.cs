using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PidSimulator.App.Views;
using PidSimulator.Core;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;
using PidSimulator.Plc.Slmp;
using PidSimulator.Plc.HostLink;
using PidSimulator.Plc.Modbus;

namespace PidSimulator.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const string FileFilter = "PIDシミュレータ プロジェクト (*.psim)|*.psim";

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private DateTime _toastUntil;
    private int _tick;
    private CancellationTokenSource? _startAllCts;

    public MainViewModel(SimulationEngine engine, DummyPlc dummy, string projectName)
    {
        Engine = engine;
        Dummy = dummy;
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
        _timer.Start();
        UpdateSummary();
    }

    public SimulationEngine Engine { get; }
    /// <summary>PLCなしで動かすときのダミーPLC（PLC側PIDの模擬を含む）。常に保持し、通信方式で切り替える。</summary>
    public DummyPlc Dummy { get; }
    public bool IsDummy => Engine.Plc is DummyPlc;
    public bool CanConnect => Engine.Plc is IConnectablePlcClient && Engine.Plc.Status.State == PlcConnectionState.Disconnected;
    public bool CanDisconnect => Engine.Plc is IConnectablePlcClient && Engine.Plc.Status.State != PlcConnectionState.Disconnected;
    public PlcSettings PlcSettings { get; private set; } = new();
    public DataSettings DataSettings { get; private set; } = new();
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

    private void UpdateSummary()
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
        var candidates = Targets.Where(t => t.IsStopped).ToList();
        if (candidates.Count == 0) { Notify("停止中の制御対象はありません"); return; }
        var blockers = candidates.ToDictionary(t => t, t => TargetBlockers(t.Model));
        var ok = candidates.Where(t => !t.HasAlarm && blockers[t].Count == 0).ToList();
        var ng = candidates.Where(t => t.HasAlarm).ToList();

        var items = ok.Select(t => new CheckResult(CheckLevel.Ok, $"{t.Name}　PV → {t.PvAddress}"))
            .Concat(ng.Select(t => new CheckResult(CheckLevel.Warn, $"開始しない（異常中）: {t.Name}")))
            .Concat(candidates.SelectMany(t => blockers[t].Select(c => new CheckResult(CheckLevel.Block, $"{t.Name}: {c.Message}"))));
        if (ok.Count == 0) { Dialogs.Error("全制御開始", "開始できる制御対象がありません。", items); return; }
        if (!Dialogs.Confirm("全制御開始", "次の制御対象の演算とPV書込みを開始します。", $"{ok.Count} 件を開始", items: items)) return;

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
                    Dialogs.Error("全制御を開始できません", result.Message);
                    return;
                }
            }
            cts.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(plc, Engine.Plc)) return;
            if (plc.Status.State != PlcConnectionState.Connected)
            {
                Dialogs.Error("全制御を開始できません", "PLCに接続できませんでした。");
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
    private void Register()
    {
        var cfg = TargetConfig.Default(ModelKind.Level);
        (cfg.MvAddress, cfg.PvAddress, cfg.SpAddress) = NextAddresses(cfg.DataType, cfg.MvOnOff);
        var vm = new RegistrationViewModel(RegistrationMode.New, cfg, Engine.Targets, Engine.Plc, plcSettings: PlcSettings);
        if (!ShowRegistration(vm)) return;
        var t = AddTarget(vm.Result!, null);
        Notify($"{t.Name} を登録しました");
    }

    public void Edit(TargetViewModel t)
    {
        if (!t.IsStopped)
        {
            Dialogs.Info("登録内容の編集", $"{t.Name} は制御中またはプレビュー中です。停止してから編集してください。");
            return;
        }
        var vm = new RegistrationViewModel(RegistrationMode.Edit, t.Model.ToConfig(),
            Engine.Targets.Where(x => x != t.Model), Engine.Plc, t.Name, PlcSettings);
        if (!ShowRegistration(vm)) return;

        var cfg = vm.Result!;
        ControlTarget updated;
        if (cfg.Kind != t.Model.Kind)
        {
            updated = ControlTarget.FromConfig(cfg);
            Engine.Replace(t.Model, updated);
        }
        else
        {
            t.Model.ApplySettings(cfg);
            Engine.Log.Add(t.Model, "登録", "登録内容を編集");
            updated = t.Model;
        }
        ReplaceViewModel(t, new TargetViewModel(this, updated));
        RebuildDummyLoops();
        MarkDirty();
        Notify("登録内容を保存しました");
    }

    public void Duplicate(TargetViewModel t)
    {
        var cfg = t.Model.ToConfig();
        cfg.Name = $"{t.Name} のコピー";
        (cfg.MvAddress, cfg.PvAddress, cfg.SpAddress) = NextAddresses(cfg.DataType, cfg.MvOnOff);
        var vm = new RegistrationViewModel(RegistrationMode.Duplicate, cfg, Engine.Targets, Engine.Plc, t.Name, PlcSettings);
        if (!ShowRegistration(vm)) return;
        var added = AddTarget(vm.Result!, $"{t.Name} から複製して登録");
        Notify($"{added.Name} を登録しました");
    }

    public void Delete(TargetViewModel t)
    {
        if (!t.IsStopped)
        {
            Dialogs.Info("削除できません", $"{t.Name} は制御中です。停止してから削除してください。");
            return;
        }
        if (!Dialogs.Confirm("制御対象の削除", $"{t.Name} を削除します。トレンド履歴と設定も削除されます。\nPV書込先 {t.PvAddress} への書込みは行われなくなります。", "削除", danger: true))
            return;
        if (CurrentPage is DetailViewModel d && d.Target == t) Back();
        Engine.Remove(t.Model);
        Targets.Remove(t);
        RebuildDummyLoops();
        MarkDirty();
        UpdateSummary();
        Notify("削除しました");
    }

    private static bool ShowRegistration(RegistrationViewModel vm)
    {
        var window = new RegistrationWindow(vm) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true && vm.Result != null;
    }

    private TargetViewModel AddTarget(TargetConfig cfg, string? extraLog)
    {
        var t = ControlTarget.FromConfig(cfg);
        Engine.Add(t);
        if (extraLog != null) Engine.Log.Add(t, "登録", extraLog);
        Dummy.AddLoop(DummyPidLoop.For(t));
        var vm = new TargetViewModel(this, t);
        Targets.Add(vm);
        MarkDirty();
        UpdateSummary();
        return vm;
    }

    private void ReplaceViewModel(TargetViewModel oldVm, TargetViewModel newVm)
    {
        int i = Targets.IndexOf(oldVm);
        if (i >= 0) Targets[i] = newVm;
        if (CurrentPage is DetailViewModel d && d.Target == oldVm)
        {
            Open(newVm);
        }
    }

    /// <summary>ダミーPLCのPIDループを登録内容に合わせて作り直す（実機PLCでは不要）</summary>
    private void RebuildDummyLoops()
    {
        var sps = Engine.Targets.ToDictionary(t => t, t =>
        {
            var previous = Dummy.FindLoop(t.MvAddress);
            return t.UseSp && previous?.SpRange.Unit == t.SpRange.Unit ? previous.Sp : t.InternalSp;
        });
        Dummy.ClearLoops();
        foreach (var (t, sp) in sps) Dummy.AddLoop(DummyPidLoop.For(t, sp));
    }

    /// <summary>選択中の通信方式に合うワードデバイスで、既存対象と重ならないアドレスを提案する。</summary>
    private (string mv, string pv, string sp) NextAddresses(string dataType = "INT16", bool mvOnOff = false)
    {
        if (PlcSettings.Mode == PlcMode.ModbusTcp)
        {
            int input = NextModbusRegister("IR"), holding = NextModbusRegister("HR");
            int mvWords = !mvOnOff && dataType is "INT32" or "FLOAT32" ? 2 : 1;
            return ($"IR{input}{(mvOnOff ? ".0" : "")}", $"HR{holding}", $"IR{input + mvWords}");
        }
        string prefix = PlcSettings.Mode == PlcMode.HostLink && !PlcSettings.Profile.EndsWith("-xym", StringComparison.Ordinal) ? "DM" : "D";
        int max = Engine.Targets
            .SelectMany(t => new[] { t.MvAddress, t.PvAddress, t.SpAddress })
            .Select(a => Regex.Match(a ?? "", $@"^{prefix}(\d+)$", RegexOptions.IgnoreCase))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(990)
            .Max();
        int b = (max / 10 + 1) * 10;
        return ($"{prefix}{b}", $"{prefix}{b + 2}", $"{prefix}{b + 4}"); // FLOAT32 / INT32 は各2ワード使う
    }

    private int NextModbusRegister(string device)
    {
        int next = 0;
        foreach (var target in Engine.Targets)
        foreach (var (address, type) in new[]
        {
            (target.MvAddress, target.MvDataType), (target.PvAddress, target.DataType),
            (target.UseSp ? target.SpAddress : "", target.DataType),
        })
        {
            if (!ModbusAddressRules.TryParse(address, out var parsed) || parsed.Device != device) continue;
            next = Math.Max(next, parsed.Number + (type is "INT32" or "FLOAT32" ? 2 : 1));
        }
        return next;
    }

    // ---- プロジェクト（§13） ----

    [RelayCommand]
    private void NewProject()
    {
        if (!ConfirmLeave("新規プロジェクト")) return;
        LoadDocument(new ProjectDocument(), null);
        Notify("新規プロジェクトを作成しました");
    }

    [RelayCommand]
    private void OpenProject()
    {
        if (!ConfirmLeave("プロジェクトを開く")) return;
        var dlg = new OpenFileDialog { Filter = FileFilter, Title = "プロジェクトを開く" };
        if (dlg.ShowDialog() != true) return;
        OpenProjectFile(dlg.FileName);
    }

    [RelayCommand]
    private void OpenDemo()
    {
        if (!ConfirmLeave("DEMOを開く")) return;
        OpenProjectFile(Path.Combine(AppContext.BaseDirectory, ProjectSerializer.DemoFileName));
    }

    private void OpenProjectFile(string path)
    {
        try
        {
            var doc = ProjectSerializer.Load(path);
            LoadDocument(doc, path);
            Notify($"{Path.GetFileName(path)} を開きました（{doc.Targets.Count} 件）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Dialogs.Error("プロジェクトを開けません", $"{path}\n\n{ex.Message}");
        }
    }

    [RelayCommand]
    private void SaveProject() => Save();

    [RelayCommand]
    private void SaveProjectAs() => SaveAs();

    private bool Save() => ProjectPath == null || ProjectSerializer.IsDemoFile(ProjectPath)
        ? SaveAs() : WriteProject(ProjectPath);

    private bool SaveAs()
    {
        var dlg = new SaveFileDialog
        {
            Filter = FileFilter,
            Title = "名前を付けて保存",
            FileName = ProjectSerializer.IsDemoFile(ProjectPath) ? "DEMO_コピー" : Path.GetFileNameWithoutExtension(ProjectPath) ?? ProjectName,
            DefaultExt = ProjectSerializer.Extension,
        };
        dlg.FileOk += (_, e) =>
        {
            if (!ProjectSerializer.IsDemoFile(dlg.FileName)) return;
            e.Cancel = true;
            Dialogs.Info("別の名前で保存", "DEMO.psim は上書きできません。ファイル名を変更してください。");
        };
        if (dlg.ShowDialog() != true) return false;
        return WriteProject(dlg.FileName);
    }

    private bool WriteProject(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        var doc = new ProjectDocument
        {
            Name = name,
            Plc = PlcSettings,
            Data = DataSettings,
            Targets = Engine.Targets.Select(t => t.ToConfig()).ToList(),
        };
        try
        {
            ProjectSerializer.Save(path, doc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error("保存できません", $"{path}\n\n{ex.Message}");
            return false;
        }
        ProjectPath = path;
        ProjectName = name;
        IsDirty = false;
        Engine.Log.Add(null, "登録", $"プロジェクトを保存: {Path.GetFileName(path)}");
        Notify($"{Path.GetFileName(path)} に保存しました");
        return true;
    }

    private void LoadDocument(ProjectDocument doc, string? path)
    {
        _startAllCts?.Cancel();
        (CurrentPage as DetailViewModel)?.Detach();
        CurrentPage = Overview;
        Engine.Clear();
        Dummy.ClearLoops(resetState: true);
        Targets.Clear();
        DataSettings = doc.Data;
        Engine.SetTrendRetention(DataSettings.TrendMinutes);
        ApplyPlcSettings(doc.Plc, force: true);
        foreach (var cfg in doc.Targets)
        {
            var t = ControlTarget.FromConfig(cfg);
            Engine.Add(t);
            Dummy.AddLoop(DummyPidLoop.For(t));
            Targets.Add(new TargetViewModel(this, t));
        }
        ProjectPath = path;
        ProjectName = path != null ? Path.GetFileNameWithoutExtension(path) : doc.Name;
        IsDirty = false;
        Engine.Log.Add(null, "登録", path != null ? $"プロジェクトを開く: {Path.GetFileName(path)}" : "新規プロジェクト");
        UpdateSummary();
    }

    /// <summary>プロジェクトを離れる前の確認。制御中なら停止、未保存なら保存を尋ねる。</summary>
    public bool ConfirmLeave(string action)
    {
        if (Targets.Any(t => !t.IsStopped))
        {
            if (!Dialogs.Confirm(action, "制御中またはプレビュー中の対象があります。すべて停止してから続けます。", "停止して続ける", danger: true))
                return false;
            Engine.StopAll(action);
        }
        if (!IsDirty) return true;
        return Dialogs.AskSave(ProjectName) switch
        {
            0 => Save(),
            1 => true,
            _ => false,
        };
    }

    // ---- 共通設定・その他 ----

    [RelayCommand]
    private void OpenSettings(string? tab)
    {
        var vm = new SettingsViewModel(PlcSettings, DataSettings, int.TryParse(tab, out int i) ? i : 0);
        var window = new SettingsWindow(vm) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true || !vm.Saved) return;
        var next = PlcSettings.Clone();
        var nextData = new DataSettings();
        vm.ApplyTo(next, nextData);
        if (!ApplyPlcSettings(next, force: false)) return;
        DataSettings = nextData;
        Engine.SetTrendRetention(DataSettings.TrendMinutes);
        MarkDirty();
        Notify("共通設定を保存しました");
    }

    /// <summary>
    /// 通信方式・接続先を切り替える。制御中の対象があれば確認して全停止してから切り替える。
    /// 実機用クライアントは未接続の状態で保持し、接続操作まで通信を開始しない。
    /// </summary>
    private bool ApplyPlcSettings(PlcSettings next, bool force)
    {
        bool changed = force || !next.SameConnection(PlcSettings);
        if (!changed)
        {
            PlcSettings = next;
            return true;
        }
        _startAllCts?.Cancel();

        if (Targets.Any(t => !t.IsStopped))
        {
            if (!Dialogs.Confirm("PLC通信の切替", "制御中またはプレビュー中の対象があります。すべて停止してから通信方式を切り替えます。", "停止して切り替える", danger: true))
                return false;
            Engine.StopAll("PLC通信の切替");
        }

        IPlcClient client;
        try
        {
            client = CreatePlcClient(next);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            Dialogs.Error("通信設定を保存できません", $"接続設定を確認してください。\n\n{ex.Message}");
            return false;
        }
        PlcSettings = next;
        var old = Engine.SwapPlc(client);
        if (!ReferenceEquals(old, client) && old is not DummyPlc) _ = Task.Run(old.Dispose);
        foreach (var t in Targets) t.Refresh(false);
        OnPropertyChanged(nameof(IsDummy));
        UpdatePlcStatus();
        return true;
    }

    private IPlcClient CreatePlcClient(PlcSettings settings) => settings.Mode switch
    {
        PlcMode.Slmp => new SlmpPlcClient(settings),
        PlcMode.HostLink => new HostLinkPlcClient(settings),
        PlcMode.ModbusTcp => new ModbusPlcClient(settings),
        _ => Dummy,
    };

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void ConnectPlc()
    {
        if (Engine.Plc is not IConnectablePlcClient client) return;
        client.Connect();
        UpdatePlcStatus();
        Notify("PLCへの接続を開始しました");
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectPlc()
    {
        _startAllCts?.Cancel();
        Engine.StopAll("通信切断");
        var old = Engine.SwapPlc(CreatePlcClient(PlcSettings));
        await Task.Run(old.Dispose);
        foreach (var t in Targets) t.Refresh(false);
        UpdatePlcStatus();
        Notify("通信を切断しました");
    }

    public bool CheckPlcConnected()
    {
        if (Engine.Plc.Status.State == PlcConnectionState.Connected) return true;
        Dialogs.Info("PLC未接続", "「接続」を押し、接続完了後に制御を開始してください。");
        return false;
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (CurrentPage is DetailViewModel d) d.ExportCsvCommand.Execute(null);
        else Dialogs.Info("試験データのCSV出力", "一覧から制御対象を開き、詳細画面で実行してください。");
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
