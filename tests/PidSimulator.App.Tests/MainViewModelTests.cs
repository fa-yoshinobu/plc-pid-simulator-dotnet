using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Threading;
using PidSimulator.App;
using PidSimulator.App.ViewModels;
using PidSimulator.Core;
using PidSimulator.Core.Models;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;
using PidSimulator.Plc.Modbus;

namespace PidSimulator.App.Tests;

public class MainViewModelTests
{
    [Fact]
    public void ModbusSettingsSelectTheRealClientAndSurviveProjectSaveAndOpen() => OnSta(() =>
    {
        using var session = new Session(useRealClients: true);
        session.Ui.SettingsEdit = settings =>
        {
            settings.Mode = PlcMode.ModbusTcp;
            settings.Host = "127.0.0.1";
            settings.ModbusUnitId = 7;
            settings.ModbusWordOrder = ModbusWordOrder.LowWordFirst;
            settings.ModbusTestAddress = "DI3";
        };
        session.Main.OpenSettingsCommand.Execute("0");
        Assert.IsType<ModbusPlcClient>(session.Engine.Plc);
        session.Main.RegisterCommand.Execute(null);
        var target = Assert.Single(session.Main.Targets);
        Assert.Equal("IR0", target.MvAddress);
        Assert.Equal("HR0", target.PvAddress);
        Assert.Equal("IR1", target.Model.SpAddress);
        session.Ui.SavePath = session.Path("modbus.psim");
        session.Main.SaveProjectCommand.Execute(null);
        session.Main.NewProjectCommand.Execute(null);
        session.Ui.OpenPath = session.Ui.SavePath;
        session.Main.OpenProjectCommand.Execute(null);
        Assert.IsType<ModbusPlcClient>(session.Engine.Plc);
        Assert.Equal(PlcMode.ModbusTcp, session.Main.PlcSettings.Mode);
        Assert.Equal(7, session.Main.PlcSettings.ModbusUnitId);
        Assert.Equal(ModbusWordOrder.LowWordFirst, session.Main.PlcSettings.ModbusWordOrder);
        Assert.Equal("DI3", session.Main.PlcSettings.ModbusTestAddress);
        Assert.Equal("HR0", Assert.Single(session.Main.Targets).PvAddress);
        Assert.Empty(session.Ui.Errors);
    });

    [Fact]
    public void ModbusDuplicateReservesBothWordsOfThirtyTwoBitRegisters() => OnSta(() =>
    {
        using var session = new Session { Factory = _ => new FakePlc() };
        Assert.True(session.Main.ApplyPlcSettings(new PlcSettings
        {
            Mode = PlcMode.ModbusTcp, Host = "127.0.0.1", Port = 502,
        }, force: false));
        session.Ui.TargetEdit = target => { target.DataType = "FLOAT32"; target.SpAddress = "IR2"; };
        session.Main.RegisterCommand.Execute(null);
        var first = Assert.Single(session.Main.Targets);
        session.Ui.TargetEdit = target => target.Name = "LIC-102";
        session.Main.Duplicate(first);
        Assert.Equal(2, session.Main.Targets.Count);
        var copy = session.Main.Targets[1];
        Assert.Equal("IR4", copy.MvAddress);
        Assert.Equal("HR2", copy.PvAddress);
        Assert.Equal("IR6", copy.Model.SpAddress);
        Assert.Equal("FLOAT32", copy.DataType);
        Assert.Empty(session.Ui.Errors);
    });

    [Fact]
    public void EventRetentionWarnsOnceBeforeDeletingAndKeepsTheSameUiLimit() => OnSta(() =>
    {
        using var session = new Session();
        for (int i = 0; i < EventLog.Capacity - 1; i++) session.Engine.Log.Add(null, "test", $"entry-{i}");
        Assert.Empty(session.Ui.Information);
        session.Engine.Log.Add(null, "test", "limit");
        Assert.Single(session.Ui.Information);
        Assert.Contains("最大5000件", session.Ui.Information[0]);
        Assert.Equal("entry-0", session.Engine.Log.Snapshot()[0].Message);
        Assert.Equal(EventLog.Capacity, session.Main.Events.Count);

        session.Engine.Log.Add(null, "test", "after-limit");
        Assert.Single(session.Ui.Information);
        Assert.Equal(EventLog.Capacity, session.Main.Events.Count);
        Assert.DoesNotContain(session.Main.Events, entry => entry.Message == "entry-0");
    });

    [Fact]
    public void NewRegisterSaveAndReopenUsesTheSameProjectAndAddresses() => OnSta(() =>
    {
        using var session = new Session();
        session.Main.NewProjectCommand.Execute(null);
        session.Main.RegisterCommand.Execute(null);
        var registered = Assert.Single(session.Main.Targets).Model.ToConfig();
        session.Ui.SavePath = session.Path("line.psim");
        session.Main.SaveProjectCommand.Execute(null);
        Assert.False(session.Main.IsDirty);
        Assert.Equal("", ProjectSerializer.Load(session.Ui.SavePath).Data.CsvFolder);

        session.Main.NewProjectCommand.Execute(null);
        Assert.Empty(session.Main.Targets);
        session.Ui.OpenPath = session.Ui.SavePath;
        session.Main.OpenProjectCommand.Execute(null);
        var reopened = Assert.Single(session.Main.Targets).Model.ToConfig();
        Assert.Equal(registered.Name, reopened.Name);
        Assert.Equal(registered.PvAddress, reopened.PvAddress);
        Assert.Equal(registered.Params, reopened.Params);
        Assert.False(session.Main.IsDirty);
        Assert.Empty(session.Ui.Errors);
    });

    [Fact]
    public void UnsavedOpenAndCloseRespectCancelAndDiscard() => OnSta(() =>
    {
        using var session = new Session();
        session.Main.RegisterCommand.Execute(null);
        session.Ui.SaveAnswer = 2;
        session.Main.OpenProjectCommand.Execute(null);
        Assert.Equal(0, session.Ui.OpenPickerCalls);
        Assert.Single(session.Main.Targets);
        Assert.False(session.Main.ConfirmLeave("終了"));

        session.Ui.SaveAnswer = 0;
        session.Ui.SavePath = null;
        Assert.False(session.Main.ConfirmLeave("終了"));
        Assert.True(session.Main.IsDirty);
        session.Ui.SaveAnswer = 1;
        Assert.True(session.Main.ConfirmLeave("終了"));
    });

    [Fact]
    public void DemoCannotBeOverwrittenEvenWhenAFilePickerReturnsItsName() => OnSta(() =>
    {
        using var session = new Session();
        string demo = System.IO.Path.Combine(AppContext.BaseDirectory, "DEMO.psim");
        byte[] original = File.ReadAllBytes(demo);
        session.Main.OpenDemoCommand.Execute(null);
        Assert.Equal(ModelCatalog.All.Count, session.Main.Targets.Count);
        session.Main.MarkDirty();
        session.Ui.SavePath = demo;
        session.Main.SaveProjectCommand.Execute(null);
        Assert.Equal(original, File.ReadAllBytes(demo));
        Assert.True(session.Main.IsDirty);
        Assert.Contains(session.Ui.Information, text => text.Contains("上書きできません"));

        session.Ui.SavePath = session.Path("demo-copy.psim");
        session.Main.SaveProjectCommand.Execute(null);
        Assert.True(File.Exists(session.Ui.SavePath));
        Assert.False(session.Main.IsDirty);
    });

    [Fact]
    public void StartAllConnectsBeforeStartingAndWritesOnlyRegisteredPv() => OnSta(() =>
    {
        using var session = new Session();
        session.Main.RegisterCommand.Execute(null);
        var plc = new FakePlc();
        session.Factory = _ => plc;
        Assert.True(session.Main.ApplyPlcSettings(new PlcSettings { Mode = PlcMode.Slmp }, force: false));
        Assert.Equal(PlcConnectionState.Disconnected, plc.Status.State);
        session.Main.StartAllCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        Assert.Equal(1, plc.ConnectCalls);
        Assert.All(session.Main.Targets, target => Assert.Equal(RunState.Running, target.Model.RunState));
        session.Engine.StepOnce();
        Assert.NotEmpty(plc.Writes);
        Assert.All(plc.Writes, address => Assert.Equal(session.Main.Targets[0].Model.PvAddress, address));
        Assert.Empty(session.Ui.Errors);
    });

    [Fact]
    public void DemoStartAllUsesTheViewModelWorkflowWithDummyPlc() => OnSta(() =>
    {
        using var session = new Session();
        session.Main.OpenDemoCommand.Execute(null);
        session.Main.StartAllCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        for (int i = 0; i < 10; i++) session.Engine.StepOnce();
        Assert.Equal(ModelCatalog.All.Count, session.Main.Targets.Count);
        Assert.All(session.Main.Targets, target => Assert.Equal(RunState.Running, target.Model.RunState));
        Assert.Empty(session.Ui.Errors);
    });

    [Fact]
    public void FailedConnectionDoesNotStartTargets() => OnSta(() =>
    {
        using var session = new Session();
        session.Main.RegisterCommand.Execute(null);
        var plc = new FakePlc { ConnectSucceeds = false };
        session.Factory = _ => plc;
        session.Main.ApplyPlcSettings(new PlcSettings { Mode = PlcMode.Slmp }, force: false);
        session.Main.StartAllCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Equal(RunState.Stopped, session.Main.Targets[0].Model.RunState);
        Assert.Empty(plc.Writes);
        Assert.Contains(session.Ui.Errors, text => text.Contains("接続失敗"));
    });

    [Fact]
    public void RunningCommunicationSwitchCanBeRefusedWithoutCreatingAnotherClient() => OnSta(() =>
    {
        using var session = new Session();
        session.Main.RegisterCommand.Execute(null);
        session.Main.StartAllCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        var original = session.Engine.Plc;
        int created = 0;
        session.Factory = _ => { created++; return new FakePlc(); };
        session.Ui.ConfirmAnswer = false;

        Assert.False(session.Main.ApplyPlcSettings(new PlcSettings { Mode = PlcMode.HostLink, Profile = "keyence:kv-8000" }, force: false));
        Assert.Same(original, session.Engine.Plc);
        Assert.Equal(0, created);
        Assert.Equal(RunState.Running, session.Main.Targets[0].Model.RunState);
        Assert.Contains(session.Ui.Confirmations, title => title == "PLC通信の切替");
    });

    [Fact]
    public void DuplicateAndDeleteKeepTheOriginalTargetAndRequireConfirmation() => OnSta(() =>
    {
        using var session = new Session();
        session.Main.RegisterCommand.Execute(null);
        var original = session.Main.Targets[0];
        session.Main.Duplicate(original);
        Assert.Equal(2, session.Main.Targets.Count);
        var copy = session.Main.Targets[1];
        Assert.NotEqual(original.Model.PvAddress, copy.Model.PvAddress);
        Assert.Equal(original.Model.GetParam("area"), copy.Model.GetParam("area"));

        session.Ui.ConfirmAnswer = false;
        session.Main.Delete(copy);
        Assert.Equal(2, session.Main.Targets.Count);
        session.Ui.ConfirmAnswer = true;
        session.Main.Delete(copy);
        Assert.Same(original, Assert.Single(session.Main.Targets));
        Assert.Same(original.Model, Assert.Single(session.Engine.Targets));
    });

    [Fact]
    public void ViewModelEditSettingsSaveAndRecoveryPreserveUnknownJson() => OnSta(() =>
    {
        using var session = new Session();
        var config = TargetConfig.Default(ModelKind.Level);
        config.Name = "retained";
        config.MvAddress = "D1000";
        config.PvAddress = "D1002";
        config.SpAddress = "D1004";
        var json = JsonNode.Parse(ProjectSerializer.ToJson(new ProjectDocument { Targets = [config] }))!;
        json["FutureRoot"] = JsonNode.Parse("""{"array":[1,null,"将来"]}""");
        json["Plc"]!["FuturePlc"] = "kept";
        json["Data"]!["FutureData"] = 42;
        json["Targets"]![0]!["FutureTarget"] = true;
        foreach (string range in new[] { "MvRange", "PvRange", "SpRange" })
            json["Targets"]![0]![range]!["FutureRange"] = range;
        string path = session.Path("future-fields.psim");
        File.WriteAllText(path, json.ToJsonString());
        session.Ui.OpenPath = path;
        session.Main.OpenProjectCommand.Execute(null);
        session.Ui.TargetEdit = model => { model.Name = "edited"; model.MvOnOff = true; };
        session.Main.Edit(session.Main.Targets[0]);
        session.Ui.SettingsEdit = settings => settings.TrendMinutesText = "60";
        session.Main.OpenSettingsCommand.Execute("1");

        string recovery = Assert.IsType<string>(session.Main.SaveRecovery());
        session.Ui.TargetEdit = model =>
        {
            model.SelectedModel = ModelCatalog.All.First(info => info.Kind != ModelKind.Level);
            model.MvOnOff = false;
        };
        session.Main.Edit(session.Main.Targets[0]);
        session.Main.SaveProjectCommand.Execute(null);
        foreach (string savedPath in new[] { recovery, path })
        {
            var saved = JsonNode.Parse(File.ReadAllText(savedPath))!;
            Assert.True(JsonNode.DeepEquals(json["FutureRoot"], saved["FutureRoot"]));
            Assert.Equal("kept", (string?)saved["Plc"]!["FuturePlc"]);
            Assert.Equal(42, (int?)saved["Data"]!["FutureData"]);
            Assert.Equal(60, (int?)saved["Data"]!["TrendMinutes"]);
            Assert.Equal(true, (bool?)saved["Targets"]![0]!["FutureTarget"]);
            Assert.Equal("edited", (string?)saved["Targets"]![0]!["Name"]);
            foreach (string range in new[] { "MvRange", "PvRange", "SpRange" })
                Assert.Equal(range, (string?)saved["Targets"]![0]![range]!["FutureRange"]);
        }
    });

    [Fact]
    public void RecoveryCancelsPendingStartEvenIfConnectionCompletesSuccessfullyLater() => OnSta(() =>
    {
        using var session = new Session();
        session.Main.RegisterCommand.Execute(null);
        var completion = new TaskCompletionSource<PlcTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var plc = new FakePlc { ConnectionCompletion = completion };
        session.Factory = _ => plc;
        session.Main.ApplyPlcSettings(new PlcSettings { Mode = PlcMode.Slmp }, force: false);
        Task start = session.Main.StartAllCommand.ExecuteAsync(null);
        Assert.False(start.IsCompleted);

        App.StopControlForRecovery(session.Main, session.Engine);
        Assert.True(plc.ConnectionToken.IsCancellationRequested);
        plc.Connect();
        completion.SetResult(new PlcTestResult(true, 0, "connected", 0));
        start.GetAwaiter().GetResult();
        session.Engine.StepOnce();

        Assert.Equal(RunState.Stopped, session.Main.Targets[0].Model.RunState);
        Assert.Empty(plc.Writes);
        Assert.Contains("キャンセル", session.Main.ToastText);
    });

    [Fact]
    public void FailedProjectConnectionKeepsOriginalProjectAndUnknownFields() => OnSta(() =>
    {
        using var session = new Session();
        session.Main.RegisterCommand.Execute(null);
        session.Ui.SavePath = session.Path("original.psim");
        session.Main.SaveProjectCommand.Execute(null);
        var originalJson = JsonNode.Parse(File.ReadAllText(session.Ui.SavePath))!;
        originalJson["OriginalUnknown"] = JsonNode.Parse("""{"keep":[true,42]}""");
        File.WriteAllText(session.Ui.SavePath, originalJson.ToJsonString());
        session.Ui.OpenPath = session.Ui.SavePath;
        session.Main.OpenProjectCommand.Execute(null);
        session.Main.MarkDirty();
        var originalTarget = session.Main.Targets[0];
        var originalPlc = session.Engine.Plc;
        var originalSettings = session.Main.PlcSettings;
        var originalData = session.Main.DataSettings;
        string? originalPath = session.Main.ProjectPath;
        string originalName = session.Main.ProjectName;
        string? originalToast = session.Main.ToastText;

        var candidate = JsonNode.Parse(originalJson.ToJsonString())!;
        candidate["Plc"]!["Mode"] = "Slmp";
        candidate["Plc"]!["Profile"] = "unknown-profile";
        candidate["Name"] = "candidate";
        candidate["CandidateUnknown"] = "must not replace the loaded snapshot";
        string candidatePath = session.Path("candidate.psim");
        string candidateText = candidate.ToJsonString();
        File.WriteAllText(candidatePath, candidateText);
        session.Factory = _ => throw new ArgumentException("unknown-profile");
        session.Ui.OpenPath = candidatePath;
        session.Main.OpenProjectCommand.Execute(null);

        Assert.Same(originalTarget, Assert.Single(session.Main.Targets));
        Assert.Same(originalTarget.Model, Assert.Single(session.Engine.Targets));
        Assert.Same(originalPlc, session.Engine.Plc);
        Assert.Same(originalSettings, session.Main.PlcSettings);
        Assert.Same(originalData, session.Main.DataSettings);
        Assert.Equal(originalPath, session.Main.ProjectPath);
        Assert.Equal(originalName, session.Main.ProjectName);
        Assert.Equal(originalToast, session.Main.ToastText);
        Assert.True(session.Main.IsDirty);
        Assert.Contains(session.Ui.Errors, error => error.Contains("unknown-profile"));
        session.Main.SaveProjectCommand.Execute(null);
        var saved = JsonNode.Parse(File.ReadAllText(originalPath!))!;
        Assert.True(JsonNode.DeepEquals(originalJson["OriginalUnknown"], saved["OriginalUnknown"]));
        Assert.Null(saved["CandidateUnknown"]);
        Assert.Equal(candidateText, File.ReadAllText(candidatePath));
    });

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "ViewModel operation did not complete.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class Session : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("pid-viewmodel-test-").FullName;
        public FakeInteraction Ui { get; } = new();
        public DummyPlc Dummy { get; } = new();
        public SimulationEngine Engine { get; }
        public MainViewModel Main { get; }
        public Func<PlcSettings, IPlcClient>? Factory { get; set; }
        public Session(bool useRealClients = false)
        {
            Engine = new SimulationEngine(Dummy);
            Func<PlcSettings, IPlcClient>? factory = useRealClients ? null
                : settings => settings.Mode == PlcMode.Dummy ? Dummy : Factory?.Invoke(settings) ?? throw new InvalidOperationException("Unexpected PLC request");
            Main = new MainViewModel(Engine, Dummy, "test", Ui, factory, startUiTimer: false);
        }
        public string Path(string name) => System.IO.Path.Combine(_directory, name);
        public void Dispose() { Engine.Dispose(); Directory.Delete(_directory, recursive: true); }
    }

    private sealed class FakeInteraction : IUserInteraction
    {
        public bool ConfirmAnswer { get; set; } = true;
        public int SaveAnswer { get; set; } = 1;
        public string? OpenPath { get; set; }
        public string? SavePath { get; set; }
        public int OpenPickerCalls { get; private set; }
        public List<string> Errors { get; } = [];
        public List<string> Information { get; } = [];
        public List<string> Confirmations { get; } = [];
        public Action<RegistrationViewModel>? TargetEdit { get; set; }
        public Action<SettingsViewModel>? SettingsEdit { get; set; }
        public bool Confirm(string title, string message, string okLabel = "OK", bool danger = false, IEnumerable<CheckResult>? items = null)
        {
            Confirmations.Add(title);
            return ConfirmAnswer;
        }
        public void Error(string title, string message, IEnumerable<CheckResult>? items = null) => Errors.Add(message);
        public void Info(string title, string message) => Information.Add(message);
        public int AskSave(string projectName) => SaveAnswer;
        public string? OpenProjectFile() { OpenPickerCalls++; return OpenPath; }
        public string? SaveProjectFile(string defaultName) => SavePath;
        public TargetConfig? EditTarget(RegistrationViewModel viewModel)
        {
            if (string.IsNullOrWhiteSpace(viewModel.Name)) viewModel.Name = "LIC-101";
            TargetEdit?.Invoke(viewModel);
            return viewModel.BuildConfig();
        }
        public bool EditSettings(SettingsViewModel viewModel)
        {
            SettingsEdit?.Invoke(viewModel);
            viewModel.SaveCommand.Execute(null);
            return viewModel.Saved;
        }
    }

    private sealed class FakePlc : IConnectablePlcClient
    {
        public string Endpoint => "fake";
        public PlcStatus Status { get; private set; } = new(PlcConnectionState.Disconnected, "fake", null, 0, 0);
        public int ConnectCalls { get; private set; }
        public bool ConnectSucceeds { get; init; } = true;
        public TaskCompletionSource<PlcTestResult>? ConnectionCompletion { get; init; }
        public CancellationToken ConnectionToken { get; private set; }
        public List<string> Writes { get; } = [];
        public void Connect() => Status = Status with { State = PlcConnectionState.Connected };
        public Task<PlcTestResult> ConnectAndWaitAsync(CancellationToken ct = default)
        {
            ConnectCalls++;
            ConnectionToken = ct;
            if (ConnectionCompletion != null) return ConnectionCompletion.Task;
            if (ConnectSucceeds) Connect();
            return Task.FromResult(new PlcTestResult(ConnectSucceeds, 0, ConnectSucceeds ? "connected" : "接続失敗", 0));
        }
        public PlcIoStatus Read(string address, string dataType, out double raw) { raw = 0; return PlcIoStatus.Ok; }
        public PlcIoStatus Write(string address, string dataType, double raw) { Writes.Add(address); return PlcIoStatus.Ok; }
        public Task<PlcTestResult> TestReadAsync(string address, string dataType, CancellationToken ct = default) =>
            Task.FromResult(new PlcTestResult(true, 0, "read", 0));
        public Task<PlcTestResult> TestWriteAsync(string address, string dataType, double raw, CancellationToken ct = default) =>
            Task.FromResult(new PlcTestResult(true, raw, "write", 0));
        public void Dispose() { }
    }
}
