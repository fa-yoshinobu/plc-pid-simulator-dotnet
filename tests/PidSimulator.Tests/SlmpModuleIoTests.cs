using System.Diagnostics;
using System.Text.Json;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;
using PidSimulator.Plc.Slmp;
using PlcComm.Slmp;

namespace PidSimulator.Tests;

public sealed class SlmpModuleIoTests
{
    public static IEnumerable<object[]> Targets()
    {
        yield return [SlmpModuleIoTarget.OwnStation, SlmpModuleIo.OwnStation];
        yield return [SlmpModuleIoTarget.ControlSystemCpu, SlmpModuleIo.ControlSystemCpu];
        yield return [SlmpModuleIoTarget.StandbySystemCpu, SlmpModuleIo.StandbySystemCpu];
        yield return [SlmpModuleIoTarget.SystemACpu, SlmpModuleIo.SystemACpu];
        yield return [SlmpModuleIoTarget.SystemBCpu, SlmpModuleIo.SystemBCpu];
        yield return [SlmpModuleIoTarget.MultipleCpu1, SlmpModuleIo.MultipleCpu1];
        yield return [SlmpModuleIoTarget.MultipleCpu2, SlmpModuleIo.MultipleCpu2];
        yield return [SlmpModuleIoTarget.MultipleCpu3, SlmpModuleIo.MultipleCpu3];
        yield return [SlmpModuleIoTarget.MultipleCpu4, SlmpModuleIo.MultipleCpu4];
    }

    public static IEnumerable<object[]> Routes()
    {
        foreach (var profile in new[] { "melsec:iq-r", "melsec:iq-f" })
            foreach (var target in Targets())
                yield return [target[0], target[1], profile];
    }

    [Fact]
    public void Project_PreservesCanonicalTargetNamesAndConnectionChanges()
    {
        Assert.Equal(SlmpModuleIoTarget.OwnStation, new PlcSettings().SlmpModuleIo);
        foreach (var target in Enum.GetValues<SlmpModuleIoTarget>())
        {
            var original = new PlcSettings { Mode = PlcMode.Slmp, SlmpModuleIo = target };
            string json = ProjectSerializer.ToJson(new ProjectDocument { Plc = original });
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(target.ToString(), parsed.RootElement.GetProperty("Plc").GetProperty("SlmpModuleIo").GetString());
            var loaded = ProjectSerializer.FromJson(json).Plc;
            Assert.Equal(target, loaded.SlmpModuleIo);
            Assert.True(original.SameConnection(loaded));
            Assert.Equal(target, loaded.Clone().SlmpModuleIo);
            foreach (var other in Enum.GetValues<SlmpModuleIoTarget>().Where(t => t != target))
            {
                loaded.SlmpModuleIo = other;
                Assert.False(original.SameConnection(loaded));
            }
        }
    }

    [Theory]
    [InlineData("melsec:iq-r")]
    [InlineData("melsec:iq-l")]
    public void GxSimulator_PreservesSelectedCpu(string profile)
    {
        foreach (var target in Enum.GetValues<SlmpModuleIoTarget>())
        {
            var settings = new PlcSettings
            {
                Mode = PlcMode.Slmp, Profile = profile, UseGxSimulator = true,
                Host = "192.168.0.10", Port = 1234, Udp = true, SlmpModuleIo = target,
            };
            var effective = settings.Effective();
            Assert.Equal("127.0.0.1", effective.Host);
            Assert.Equal(5511, effective.Port);
            Assert.False(effective.Udp);
            Assert.Equal(target, effective.SlmpModuleIo);
            using var plc = new SlmpPlcClient(settings);
            Assert.Equal(target, plc.Settings.SlmpModuleIo);
            Assert.Contains(SlmpPlcClient.ModuleIoDisplayName(target), plc.Endpoint);
        }
    }

    [Theory]
    [InlineData("99")]
    [InlineData("\"99\"")]
    [InlineData("\"UnknownCpu\"")]
    [InlineData("\"RemoteHead1\"")]
    public void InvalidPersistedTarget_IsRejectedInsteadOfDefaulting(string value)
    {
        string json = ProjectSerializer.ToJson(new ProjectDocument());
        json = json.Replace("\"SlmpModuleIo\": \"OwnStation\"", $"\"SlmpModuleIo\": {value}", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => ProjectSerializer.FromJson(json));
    }

    [Fact]
    public async Task InvalidTarget_IsNotSentToOwnStation()
    {
        await using var server = new FakeSlmpServer();
        var settings = new PlcSettings
        {
            Mode = PlcMode.Slmp, Host = "127.0.0.1", Port = server.Port,
            SlmpModuleIo = (SlmpModuleIoTarget)999,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlmpPlcClient(settings));
        var result = await SlmpPlcClient.TestConnectionAsync(settings);
        Assert.False(result.Ok);
        Assert.Contains("要求先CPU", result.Message);
        Assert.Empty(server.Routes);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryOperation_SendsSelectedCpuInThreeAndFourEFrames(SlmpModuleIoTarget target, ushort moduleIo, string profile)
    {
        await using var server = new FakeSlmpServer();
        server.SetD(100, 123);
        var settings = new PlcSettings
        {
            Mode = PlcMode.Slmp, Profile = profile, Host = "127.0.0.1", Port = server.Port,
            SlmpModuleIo = target, TimeoutMs = 500, CommCycleMs = 30,
        };
        var connectionTest = await SlmpPlcClient.TestConnectionAsync(settings);
        Assert.True(connectionTest.Ok, connectionTest.Message);
        using var plc = new SlmpPlcClient(settings);
        Assert.Contains(SlmpPlcClient.ModuleIoDisplayName(target), plc.Endpoint);

        // 登録画面の切断中テストは一時接続、接続後は共通セッションを利用する。
        var directRead = await plc.TestReadAsync("D100", "INT16");
        Assert.True(directRead.Ok, directRead.Message);
        Assert.Equal(123, directRead.Raw);
        Assert.True((await plc.TestWriteAsync("D101", "INT16", 456)).Ok);
        Assert.Equal(456, server.GetD(101));
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        Assert.True((await plc.TestReadAsync("D100", "INT16")).Ok);
        Assert.True((await plc.TestWriteAsync("D101", "INT16", 789)).Ok);
        Assert.Equal(789, server.GetD(101));

        // 演算周期で使うキャッシュ経由のランダム読込・書込も同じ要求先。
        await UntilAsync(() => plc.Read("D100", "INT16", out double raw) == PlcIoStatus.Ok && raw == 123);
        Assert.Equal(PlcIoStatus.Ok, plc.Write("D102", "INT16", 321));
        await UntilAsync(() => server.GetD(102) == 321);
        var routes = server.Routes.ToArray();
        Assert.Contains(routes, r => r.Command == 0x0401);
        Assert.Contains(routes, r => r.Command == 0x1401);
        Assert.Contains(routes, r => r.Command == 0x0403);
        Assert.Contains(routes, r => r.Command == 0x1402);
        Assert.All(routes, route =>
        {
            Assert.Equal(profile == "melsec:iq-r", route.Is4E);
            Assert.Equal(0x00, route.Network);
            Assert.Equal(0xFF, route.Station);
            Assert.Equal(moduleIo, route.ModuleIo);
            Assert.Equal(0x00, route.Multidrop);
        });
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition(), "SLMP通信が制限時間内に完了しませんでした。");
    }
}
