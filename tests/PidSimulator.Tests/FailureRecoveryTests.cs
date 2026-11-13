using System.Collections.Concurrent;
using PidSimulator.Core;
using PidSimulator.Core.Models;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public class FailureRecoveryTests
{
    [Fact]
    public void ThrowingModelStopsOnlyItsTargetAndNeverWritesItsPv()
    {
        using var plc = new RecordingPlc();
        using var engine = new SimulationEngine(plc);
        var broken = new ControlTarget("broken", ModelKind.Flow, new ThrowingModel())
        { MvAddress = "D0", PvAddress = "D2", UseSp = false, StopPv = StopPvMode.Initial };
        var healthy = new ControlTarget("healthy", ModelKind.Flow, seed: 1)
        { MvAddress = "D10", PvAddress = "D12", UseSp = false };
        engine.Add(broken);
        engine.Add(healthy);
        Assert.True(broken.Start(engine.Log, out _));
        Assert.True(healthy.Start(engine.Log, out _));
        var errors = new List<Exception>();
        engine.Error += (error, _) => errors.Add(error);

        for (int i = 0; i < 20; i++) engine.StepOnce();

        Assert.Equal(RunState.Stopped, broken.RunState);
        Assert.False(broken.PvWriting);
        Assert.Contains("broken model", broken.Alarm);
        Assert.Equal(RunState.Running, healthy.RunState);
        Assert.Equal(20, healthy.Trend.Count);
        Assert.Equal(20, plc.Writes.Count);
        Assert.All(plc.Writes, address => Assert.Equal("D12", address));
        Assert.Single(errors);
        Assert.False(broken.Start(engine.Log, out _));
        Assert.True(broken.ResetAlarm(engine.Log));
        Assert.True(broken.Start(engine.Log, out _));
    }

    [Fact]
    public void WorkerFailureIsVisibleAndStopsAllTargets()
    {
        using var plc = new RecordingPlc { TickFailure = new InvalidOperationException("tick failed") };
        using var engine = new SimulationEngine(plc);
        var target = new ControlTarget("target", ModelKind.Flow) { MvAddress = "D0", PvAddress = "D2", UseSp = false };
        engine.Add(target);
        target.Start(engine.Log, out _);
        using var reported = new ManualResetEventSlim();
        engine.Error += (_, _) => reported.Set();
        engine.Run();

        Assert.True(reported.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => !engine.IsRunning, TimeSpan.FromSeconds(2)));
        Assert.Same(plc.TickFailure, engine.Failure);
        Assert.Equal(RunState.Stopped, target.RunState);
        Assert.Empty(plc.Writes);
        Assert.Contains(engine.Log.Snapshot(), entry => entry.Message.Contains("演算スレッドが停止"));
    }

    [Fact]
    public void RecoveryCreatesUniqueFilesWithoutOverwritingTheOriginalOrDemo()
    {
        using var folder = new TestFolder();
        string original = Path.Combine(folder.Path, "DEMO.psim");
        File.WriteAllText(original, "original remains intact");
        var document = new ProjectDocument { Name = "unsaved changes" };
        string first = ProjectRecovery.Save(document, original, folder.Path);
        string second = ProjectRecovery.Save(document, original, folder.Path);

        Assert.NotEqual(first, second);
        Assert.EndsWith(".recovered.psim", first);
        Assert.Equal("original remains intact", File.ReadAllText(original));
        Assert.Equal("unsaved changes", ProjectSerializer.Load(first).Name);
    }

    [Fact]
    public void RecoveryUsesFallbackWhenTheProjectLocationIsUnavailable()
    {
        using var folder = new TestFolder();
        string blocker = Path.Combine(folder.Path, "not-a-directory");
        File.WriteAllText(blocker, "keep");
        string fallback = Path.Combine(folder.Path, "fallback");
        Exception? failure = null;
        string recovered = ProjectRecovery.Save(new ProjectDocument(), Path.Combine(blocker, "project.psim"),
            fallback, error => failure = error);

        Assert.NotNull(failure);
        Assert.Equal(StoragePath.ResolveExisting(fallback), Path.GetDirectoryName(recovered));
        Assert.True(File.Exists(recovered));
        Assert.Equal("keep", File.ReadAllText(blocker));
    }

    [Fact]
    public void DiagnosticFilesAreBoundedAndContainExceptionDetails()
    {
        using var folder = new TestFolder();
        var log = new DiagnosticLog(folder.Path, maxFileBytes: 1024, maxFiles: 3);
        for (int i = 0; i < 80; i++) log.Write("failed operation", new InvalidOperationException(new string('あ', 200)));
        var files = Directory.GetFiles(folder.Path, "*.log");
        Assert.InRange(files.Length, 1, 3);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 1024));
        Assert.Contains("InvalidOperationException", File.ReadAllText(Path.Combine(folder.Path, "pid.log")));
    }

    [Fact]
    public void LogFolderReturnsThePhysicalLocationForAnExternalShell()
    {
        using var folder = new TestFolder();
        var log = new DiagnosticLog(Path.Combine(folder.Path, "logs"));
        string physical = log.GetExistingDirectoryPath();
        Assert.True(Directory.Exists(physical));
        log.Write("path probe");
        Assert.Contains("path probe", File.ReadAllText(Path.Combine(physical, "pid.log")));
        Assert.DoesNotContain(@"\\?\", physical);
    }

    private sealed class ThrowingModel() : ProcessModel(ModelCatalog.Get(ModelKind.Flow), 1)
    {
        protected override double Integrate(double u, double d, double dt, double t) => throw new InvalidOperationException("broken model");
    }

    private sealed class RecordingPlc : IPlcClient
    {
        public string Endpoint => "test";
        public PlcStatus Status => new(PlcConnectionState.Connected, Endpoint, null, 0, 0);
        public ConcurrentQueue<string> Writes { get; } = new();
        public Exception? TickFailure { get; init; }
        public PlcIoStatus Read(string address, string dataType, out double raw) { raw = 50; return PlcIoStatus.Ok; }
        public PlcIoStatus Write(string address, string dataType, double raw) { Writes.Enqueue(address); return PlcIoStatus.Ok; }
        public void Tick(double dt) { if (TickFailure != null) throw TickFailure; }
        public Task<PlcTestResult> TestReadAsync(string address, string dataType, CancellationToken ct = default)
            => Task.FromResult(new PlcTestResult(true, 50, "test", 0));
        public Task<PlcTestResult> TestWriteAsync(string address, string dataType, double raw, CancellationToken ct = default)
            => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("pid-recovery-tests-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
