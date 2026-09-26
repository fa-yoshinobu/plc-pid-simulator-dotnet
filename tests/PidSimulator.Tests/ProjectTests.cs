using PidSimulator.Core;
using PidSimulator.Core.Models;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public class ProjectTests
{
    [Theory]
    [InlineData("DEMO.psim")]
    [InlineData("demo.psim")]
    [InlineData("Demo.PSIM")]
    public void SaveDemo_IsRejectedWithoutChangingOriginal(string name)
    {
        string folder = Path.Combine(Path.GetTempPath(), "PidSimulatorTests-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, "original demo");
            Assert.Throws<UnauthorizedAccessException>(() => ProjectSerializer.Save(path, new ProjectDocument()));
            Assert.Equal("original demo", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));

            string copy = Path.Combine(folder, "DEMO_copy.psim");
            ProjectSerializer.Save(copy, new ProjectDocument { Name = "Copy" });
            ProjectSerializer.Save(copy, new ProjectDocument { Name = "Edited copy" });
            Assert.Equal("Edited copy", ProjectSerializer.Load(copy).Name);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void DemoFile_LoadsStoppedTargets_AndRunsWithDummyPlc()
    {
        var doc = ProjectSerializer.Load(Path.Combine(AppContext.BaseDirectory, "DEMO.psim"));
        Assert.Equal(PlcMode.Dummy, doc.Plc.Mode);
        Assert.Equal(ModelCatalog.All.Count, doc.Targets.Count);
        Assert.Equal(ModelCatalog.All.Select(m => m.Kind).OrderBy(k => k), doc.Targets.Select(t => t.Kind).OrderBy(k => k));
        var plc = new DummyPlc();
        using var engine = new SimulationEngine(plc);
        foreach (var cfg in doc.Targets)
        {
            Assert.DoesNotContain(RegistrationValidator.Check(cfg, engine.Targets), r => r.Level == CheckLevel.Block);
            var target = ControlTarget.FromConfig(cfg);
            Assert.Equal(RunState.Stopped, target.RunState);
            Assert.Equal(ModelCatalog.Get(cfg.Kind).Params.Count, cfg.Params.Count);
            engine.Add(target);
            plc.AddLoop(DummyPidLoop.For(target));
            Assert.True(target.Start(engine.Log, out _));
        }
        for (int i = 0; i < 100; i++) engine.StepOnce();
        Assert.All(engine.Targets, t => Assert.Equal(RunState.Running, t.RunState));
    }

    [Fact]
    public void ResetDummyProject_ClearsInjectedFaultsAndRegisters()
    {
        var plc = new DummyPlc();
        plc.TryWrite("D1000", 123);
        plc.SetFault("D1000", true);
        plc.ClearLoops(resetState: true);
        Assert.False(plc.IsFaulted("D1000"));
        Assert.True(plc.TryRead("D1000", out var raw));
        Assert.Equal(0, raw);
    }

    private static TargetConfig Level(string name, int addr) => new TargetConfig
    {
        Name = name, MvAddress = $"D{addr}", PvAddress = $"D{addr + 1}", SpAddress = $"D{addr + 2}",
    }.Also(c => c.ApplyModelDefaults(ModelCatalog.Get(ModelKind.Level)));

    [Fact]
    public void Project_RoundTripsThroughJson_WithJapaneseAndParams()
    {
        var cfg = Level("LIC-101 原水槽 液面", 1000);
        cfg.Params["kout"] = 2.5;
        cfg.OnCommError = CommErrorAction.SafeMv;
        cfg.SafeMv = 12;
        var doc = new ProjectDocument { Name = "水処理ライン", Targets = { cfg } };

        string json = ProjectSerializer.ToJson(doc);
        Assert.Contains("原水槽", json);          // 日本語をエスケープせず読める形で保存
        Assert.Contains("\"SafeMv\"", json);
        Assert.DoesNotContain("IsValid", json);

        var back = ProjectSerializer.FromJson(json);
        var t = ControlTarget.FromConfig(back.Targets[0]);
        Assert.Equal("LIC-101 原水槽 液面", t.Name);
        Assert.Equal(2.5, t.GetParam("kout"));
        Assert.Equal(CommErrorAction.SafeMv, t.OnCommError);
        Assert.Equal(12, t.SafeMv);
        Assert.Equal(4000, t.PvRange.RawMax);
    }

    [Theory]
    [InlineData("melsec:iq-r", true)]
    [InlineData("melsec:iq-l", true)]
    [InlineData("melsec:iq-f", false)]
    [InlineData("melsec:iq-r:rj71en71", false)]
    public void GxSimulator_FixesEndpoint_OnlyForIqRAndIqL(string profile, bool expected)
    {
        var s = new PlcSettings { Mode = PlcMode.Slmp, Profile = profile, Host = "192.168.3.39", Port = 1025, Udp = true, UseGxSimulator = true };
        var e = s.Effective();
        Assert.Equal(expected, e.UseGxSimulator);
        Assert.Equal(expected ? "127.0.0.1" : "192.168.3.39", e.Host);
        Assert.Equal(expected ? 5511 : 1025, e.Port);
        Assert.Equal(!expected, e.Udp);
        Assert.Equal("192.168.3.39", s.Host); // 元の設定（実機の接続先）は残す
    }

    [Fact]
    public void GxSimulatorFlag_IsSavedInProject_AndChangesConnectionIdentity()
    {
        var doc = new ProjectDocument { Plc = { Mode = PlcMode.Slmp, UseGxSimulator = true } };
        var back = ProjectSerializer.FromJson(ProjectSerializer.ToJson(doc));
        Assert.True(back.Plc.IsGxSimulator);
        Assert.False(back.Plc.SameConnection(new PlcSettings { Mode = PlcMode.Slmp }));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public void Project_RejectsUnsupportedFormat(int version)
    {
        var json = ProjectSerializer.ToJson(new ProjectDocument { FormatVersion = version });
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.FromJson(json));
    }

    [Fact]
    public void Validator_BlocksSpRangeWithEqualEndpoints()
    {
        var c = Level("X", 3000);
        c.SpRange.EngMax = c.SpRange.EngMin;
        var results = RegistrationValidator.Check(c, []);
        Assert.Contains(results, r => r.Level == CheckLevel.Block && r.Message.Contains("SP レンジ"));
    }

    [Fact]
    public void SetParam_RejectsStopOnlyWhileRunning_AndLogsChanges()
    {
        var plc = new DummyPlc();
        var eng = new SimulationEngine(plc);
        var t = ControlTarget.FromConfig(Level("L", 1000));
        eng.Add(t);
        t.Start(eng.Log, out _);

        Assert.False(t.SetParam("area", 3, eng.Log, out var err));  // 断面積：停止中のみ
        Assert.NotNull(err);
        Assert.True(t.SetParam("qin", 40, eng.Log, out _));          // 最大流入量：運転中も可
        Assert.Contains(eng.Log.Snapshot(), e => e.Category == "パラメータ" && e.Message.Contains("最大流入量"));
    }

    [Fact]
    public void Csv_HasHeaderUnitsAndOneRowPerSample()
    {
        var t = ControlTarget.FromConfig(Level("LIC-101", 1000));
        var sw = new StringWriter();
        TrendCsv.Write(sw, t, [new TrendSample(1, 60, 59.5, 42.25, 0, false), new TrendSample(1.1, 60, 59.6, 42, 5, true, false)],
            s => new DateTime(2026, 9, 26, 10, 0, 0).AddSeconds(s));
        var lines = sw.ToString().TrimEnd().Split(Environment.NewLine);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("時刻,SP[%],PV[%],MV[%],外乱[m³/h]", lines[1]);
        Assert.Equal("2026/09/26 10:00:01.1,60,59.6,42,5,ON,異常", lines[3]);
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
