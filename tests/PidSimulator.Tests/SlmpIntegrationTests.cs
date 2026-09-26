using PidSimulator.Core;
using PidSimulator.Core.Models;
using System.Diagnostics;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;
using PidSimulator.Plc.Slmp;

namespace PidSimulator.Tests;

/// <summary>PlcComm.Slmp 経由で、テスト用SLMPサーバと実際にTCP通信する結合テスト</summary>
public class SlmpIntegrationTests
{
    [Theory]
    [InlineData("M100", 0x90, 100)]
    [InlineData("Y1A", 0x9D, 0x1A)]
    public async Task RelayBit_ReadsNativeDevices(string address, int code, int number)
    {
        await using var server = new FakeSlmpServer();
        using var plc = new SlmpPlcClient(Settings(server.Port, "melsec:iq-r"));
        server.SetBit((ushort)code, (uint)number, true);
        var result = await plc.TestReadAsync(address, "BIT");
        Assert.True(result.Ok, result.Message); Assert.Equal(1, result.Raw);
        plc.Connect();
        Assert.True(WaitUntil(() => plc.Read(address, "BIT", out var raw) == PlcIoStatus.Ok && raw == 1));
        server.SetBit((ushort)code, (uint)number, false);
        Assert.True(WaitUntil(() => plc.Read(address, "BIT", out var raw) == PlcIoStatus.Ok && raw == 0));
    }

    [Theory]
    [InlineData("D1.0")] [InlineData("D1.15")] [InlineData("D1.F")]
    public async Task RelayBit_ReadsSelectedBitInWord(string address)
    {
        await using var server = new FakeSlmpServer();
        using var plc = new SlmpPlcClient(Settings(server.Port, "melsec:iq-r"));
        server.SetD(1, unchecked((short)0x8001));
        var result = await plc.TestReadAsync(address, "BIT");
        Assert.True(result.Ok, result.Message); Assert.Equal(1, result.Raw);
        plc.Connect();
        Assert.True(WaitUntil(() => plc.Read(address, "BIT", out var raw) == PlcIoStatus.Ok && raw == 1));
        server.SetD(1, 2);
        Assert.True(WaitUntil(() => plc.Read(address, "BIT", out var raw) == PlcIoStatus.Ok && raw == 0));
    }

    [Fact]
    public async Task ConnectAndWait_ReturnsOnlyAfterConnected_AndReusesConnection()
    {
        await using var server = new FakeSlmpServer();
        using var plc = new SlmpPlcClient(Settings(server.Port, "melsec:iq-r"));
        var result = await plc.ConnectAndWaitAsync();
        Assert.True(result.Ok, result.Message);
        Assert.Equal(PlcConnectionState.Connected, plc.Status.State);
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        Assert.True((await plc.TestReadAsync("D1000", "INT16")).Ok);
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task ConnectAndWait_ReportsConnectionFailure()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var plc = new SlmpPlcClient(Settings(port, "melsec:iq-r"));
        var result = await plc.ConnectAndWaitAsync();
        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task ConnectAndWait_CanceledRequestDoesNotConnect()
    {
        await using var server = new FakeSlmpServer();
        using var plc = new SlmpPlcClient(Settings(server.Port, "melsec:iq-r"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plc.ConnectAndWaitAsync(cts.Token));
        Assert.Equal(PlcConnectionState.Disconnected, plc.Status.State);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task SavedSettings_DoNotConnectUntilExplicitConnect()
    {
        await using var server = new FakeSlmpServer();
        var saved = ProjectSerializer.FromJson(ProjectSerializer.ToJson(new ProjectDocument
        {
            Plc = Settings(server.Port, "melsec:iq-r"),
        }));
        using var plc = new SlmpPlcClient(saved.Plc);
        Assert.Equal(PlcConnectionState.Disconnected, plc.Status.State);
        Assert.Equal(PlcIoStatus.Failed, plc.Read("D1000", "INT16", out _));
        await Task.Delay(150);
        Assert.Equal(0, server.ConnectionCount);
        Assert.Empty(server.Commands);

        plc.Connect();
        plc.Connect();
        Assert.True(WaitUntil(() => plc.Status.State == PlcConnectionState.Connected));
        Assert.True((await plc.TestReadAsync("D1000", "INT16")).Ok);
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task DisconnectedTests_ConnectTemporarily_WithoutStartingBackgroundCommunication()
    {
        await using var server = new FakeSlmpServer();
        server.SetD(1000, 456);
        using var plc = new SlmpPlcClient(Settings(server.Port, "melsec:iq-r"));
        var read = await plc.TestReadAsync("D1000", "INT16");
        Assert.True(read.Ok, read.Message);
        Assert.Equal(456, read.Raw);
        var write = await plc.TestWriteAsync("D1001", "INT16", 123);
        Assert.True(write.Ok, write.Message);
        Assert.Equal(123, server.GetD(1001));
        Assert.Equal(PlcConnectionState.Disconnected, plc.Status.State);
        int connections = server.ConnectionCount;
        int commands = server.Commands.Count;
        await Task.Delay(150);
        Assert.Equal(connections, server.ConnectionCount);
        Assert.Equal(commands, server.Commands.Count);
        Assert.Equal(PlcIoStatus.Failed, plc.Read("D1000", "INT16", out _));
    }

    private static PlcSettings Settings(int port, string profile) => new()
    {
        Mode = PlcMode.Slmp, Profile = profile, Host = "127.0.0.1", Port = port, TimeoutMs = 500, CommCycleMs = 50,
    };

    private static bool WaitUntil(Func<bool> condition, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    private static ControlTarget LevelTarget(int addr) => ControlTarget.FromConfig(new TargetConfig
    {
        Name = "LIC-101", MvAddress = $"D{addr}", PvAddress = $"D{addr + 1}", SpAddress = $"D{addr + 2}",
    }.Also(c => c.ApplyModelDefaults(ModelCatalog.Get(ModelKind.Level))));

    [Theory]
    [InlineData("melsec:iq-r")]   // 4E フレーム・iQ-R デバイス指定
    [InlineData("melsec:iq-f")]   // 3E フレーム・レガシーデバイス指定
    public async Task RunningTarget_ReadsMvSpAndWritesPv_OverSlmp(string profile)
    {
        await using var server = new FakeSlmpServer();
        server.SetD(1000, 2000);  // MV 50 %
        server.SetD(1002, 2400);  // SP 60 %

        using var plc = new SlmpPlcClient(Settings(server.Port, profile));
        plc.Connect();
        using var engine = new SimulationEngine(plc);
        var t = LevelTarget(1000);
        engine.Add(t);
        Assert.True(WaitUntil(() => plc.Status.State == PlcConnectionState.Connected), plc.Status.LastError);

        Assert.True(t.Start(engine.Log, out _));
        engine.Run();

        Assert.True(WaitUntil(() => Math.Abs(t.Snapshot().Mv - 50) < 0.01), $"MV={t.Snapshot().Mv}");
        Assert.True(WaitUntil(() => Math.Abs(t.Snapshot().Sp - 60) < 0.01), $"SP={t.Snapshot().Sp}");
        Assert.True(WaitUntil(() => server.GetD(1001) == PlcDataTypes.Clamp(t.DataType, t.PvRange.ToRaw(t.Snapshot().Pv)) && server.GetD(1001) != 0, 3000),
            $"PLC D1001={server.GetD(1001)} / PV raw={t.PvRange.ToRaw(t.Snapshot().Pv)}");
        Assert.Equal(CommStatus.Ok, t.Snapshot().Comm);
        Assert.Contains((ushort)0x0403, server.Commands); // Random Read
        Assert.Contains((ushort)0x1402, server.Commands); // Random Write

        // PLC側でSPを変えると、次の通信周期で反映される
        server.SetD(1002, 3200);
        Assert.True(WaitUntil(() => Math.Abs(t.Snapshot().Sp - 80) < 0.01), $"SP={t.Snapshot().Sp}");
    }

    [Fact]
    public async Task InvalidAddress_FailsOnlyThatTarget_OthersKeepRunning()
    {
        await using var server = new FakeSlmpServer();
        server.SetD(1000, 2000);
        using var plc = new SlmpPlcClient(Settings(server.Port, "melsec:iq-r"));
        plc.Connect();
        using var engine = new SimulationEngine(plc);
        var good = LevelTarget(1000);
        var bad = LevelTarget(1010);
        bad.ApplySettings(bad.ToConfig().Also(c => { c.Name = "BAD"; c.MvAddress = $"D{FakeSlmpServer.BadFrom + 5}"; }));
        engine.Add(good);
        engine.Add(bad);
        Assert.True(WaitUntil(() => plc.Status.State == PlcConnectionState.Connected));
        good.Start(engine.Log, out _);
        bad.Start(engine.Log, out _);
        engine.Run();

        Assert.True(WaitUntil(() => bad.Snapshot().Comm != CommStatus.Ok), "不正アドレスの対象が異常にならない");
        Assert.Contains("0xC056", bad.Snapshot().Alarm);
        Assert.True(WaitUntil(() => Math.Abs(good.Snapshot().Mv - 50) < 0.01));
        Assert.Equal(CommStatus.Ok, good.Snapshot().Comm);
        Assert.Equal(RunState.Running, good.Snapshot().RunState);
    }

    [Fact]
    public async Task ConnectionDrop_RaisesAlarm_ThenClientReconnects()
    {
        await using var server = new FakeSlmpServer();
        server.SetD(1000, 2000);
        using var plc = new SlmpPlcClient(Settings(server.Port, "melsec:iq-r"));
        plc.Connect();
        using var engine = new SimulationEngine(plc);
        var t = LevelTarget(1000);
        t.OnCommError = CommErrorAction.HoldMv;
        t.Recover = RecoverMode.Auto;
        engine.Add(t);
        Assert.True(WaitUntil(() => plc.Status.State == PlcConnectionState.Connected));
        t.Start(engine.Log, out _);
        engine.Run();
        Assert.True(WaitUntil(() => Math.Abs(t.Snapshot().Mv - 50) < 0.01));

        server.DropConnections();
        Assert.True(WaitUntil(() => server.ConnectionCount >= 2, 8000), "再接続しない");
        Assert.True(WaitUntil(() => plc.Status.State == PlcConnectionState.Connected));
        Assert.True(WaitUntil(() => t.Snapshot().Comm == CommStatus.Ok, 8000), t.Snapshot().Alarm);
        Assert.Equal(RunState.Running, t.Snapshot().RunState); // MV保持＋自動再開なので運転は継続
    }

    [Fact]
    public void NoPlc_ReadsGoPendingThenFailed_AndStatusShowsReason()
    {
        // 空いているポートを取って閉じる → 接続拒否になる
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        using var plc = new SlmpPlcClient(Settings(port, "melsec:iq-r"));
        plc.Connect();
        Assert.Equal(PlcIoStatus.Pending, plc.Read("D1000", "INT16", out _));
        Assert.True(WaitUntil(() => plc.Status.State == PlcConnectionState.Faulted));
        Assert.StartsWith("接続できません", plc.Status.LastError);
        Assert.True(WaitUntil(() => plc.Read("D1000", "INT16", out _) == PlcIoStatus.Failed, 3000));
        Assert.Equal(PlcIoStatus.Failed, plc.Write("D1001", "INT16", 100));
    }

    [Fact]
    public async Task TestReadWrite_AndConnectionTest_UseThePlcDirectly()
    {
        await using var server = new FakeSlmpServer();
        server.SetD(500, -123);
        var settings = Settings(server.Port, "melsec:iq-r");

        var conn = await SlmpPlcClient.TestConnectionAsync(settings);
        Assert.True(conn.Ok, conn.Message);

        using var plc = new SlmpPlcClient(settings);
        plc.Connect();
        Assert.True(WaitUntil(() => plc.Status.State == PlcConnectionState.Connected));
        var r = await plc.TestReadAsync("d500", "INT16");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(-123, r.Raw);

        var w = await plc.TestWriteAsync("D501", "INT16", 70000); // INT16 範囲に制限される
        Assert.True(w.Ok, w.Message);
        Assert.Equal(short.MaxValue, server.GetD(501));
    }
}
