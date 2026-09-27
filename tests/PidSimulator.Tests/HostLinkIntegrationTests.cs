using System.Diagnostics;
using PidSimulator.Core;
using PidSimulator.Core.Models;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;
using PidSimulator.Plc.HostLink;

namespace PidSimulator.Tests;

/// <summary>実際の TCP/UDP ソケットを通して変換・通信周期・復旧を確認する。</summary>
public class HostLinkIntegrationTests
{
    private static PlcSettings Settings(int port, bool udp = false, string profile = "keyence:kv-8000") => new()
    {
        Mode = PlcMode.HostLink, Profile = profile, Host = "127.0.0.1", Port = port,
        Udp = udp, TimeoutMs = 200, CommCycleMs = 30,
    };

    private static async Task UntilAsync(Func<bool> condition, int timeout = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeout)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition(), "通信結果が制限時間内に反映されませんでした。");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NumericTypes_RoundTripDirectAndBackground_WithExactInt32AndFloat(bool udp)
    {
        await using var server = new FakeHostLinkServer(udp);
        using var plc = new HostLinkPlcClient(Settings(server.Port, udp));
        var signals = new[]
        {
            (Address: "DM0", Type: "INT16", Value: -32768d),
            (Address: "DM2", Type: "UINT16", Value: 65535d),
            (Address: "DM4", Type: "INT32", Value: 2147483647d),
            (Address: "DM6", Type: "FLOAT32", Value: -12.375d),
        };
        foreach (var point in signals)
        {
            var write = await plc.TestWriteAsync(point.Address, point.Type, point.Value);
            Assert.True(write.Ok, write.Message);
            var read = await plc.TestReadAsync(point.Address, point.Type);
            Assert.True(read.Ok, read.Message);
            Assert.Equal(point.Value, read.Raw);
        }
        Assert.Equal(int.MaxValue, server.GetInt32("DM4"));
        Assert.Equal(-12.375f, server.GetFloat("DM6"));
        Assert.Equal(PlcConnectionState.Disconnected, plc.Status.State);
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        foreach (var point in signals)
        {
            await UntilAsync(() => plc.Read(point.Address, point.Type, out double raw) == PlcIoStatus.Ok && raw == point.Value);
            Assert.Equal(PlcIoStatus.Ok, plc.Write(point.Address, point.Type, point.Value / 2));
        }
        foreach (var point in signals)
            await UntilAsync(() => plc.Read(point.Address, point.Type, out double raw) == PlcIoStatus.Ok
                && raw == PlcDataTypes.Clamp(point.Type, point.Value / 2));
        Assert.Contains(server.Commands, c => c.StartsWith("RDS ", StringComparison.Ordinal));
        Assert.Contains("WRS DM6.U 2 0 49478", server.Commands); // -12.375 as low/high IEEE754 words
        Assert.False((await plc.TestWriteAsync("DM6", "FLOAT32", double.MaxValue)).Ok);
        Assert.Equal(PlcIoStatus.Failed, plc.Write("DM6", "FLOAT32", double.NaN));
    }

    [Theory]
    [InlineData("keyence:kv-8000", "R100")]
    [InlineData("keyence:kv-8000", "MR101")]
    [InlineData("keyence:kv-8000", "B1A")]
    [InlineData("keyence:kv-8000-xym", "X1A")]
    [InlineData("keyence:kv-8000-xym", "Y1F")]
    [InlineData("keyence:kv-8000-xym", "M100")]
    public async Task RelayDevices_ReadOnOffAndWriteBoolean(string profile, string address)
    {
        await using var server = new FakeHostLinkServer();
        using var plc = new HostLinkPlcClient(Settings(server.Port, profile: profile));
        server.SetBit(address, true);
        var test = await plc.TestReadAsync(address, "BIT");
        Assert.True(test.Ok, test.Message);
        Assert.Equal(1, test.Raw);
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        await UntilAsync(() => plc.Read(address, "BIT", out double raw) == PlcIoStatus.Ok && raw == 1);
        server.SetBit(address, false);
        await UntilAsync(() => plc.Read(address, "BIT", out double raw) == PlcIoStatus.Ok && raw == 0);
        var write = await plc.TestWriteAsync(address, "BIT", 1);
        Assert.True(write.Ok, write.Message);
        Assert.Equal(1, server.GetWord(address));
        Assert.Contains($"WR {address} 1", server.Commands);
    }

    [Theory]
    [InlineData("DM1.0", 0)] [InlineData("DM1.15", 15)] [InlineData("DM1.F", 15)]
    public async Task WordBit_UsesOneSelectedBitAndPreservesOtherBitsWhenWritten(string address, int bit)
    {
        await using var server = new FakeHostLinkServer();
        using var plc = new HostLinkPlcClient(Settings(server.Port));
        server.SetWord("DM1", 0x8001);
        Assert.Equal(1, (await plc.TestReadAsync(address, "BIT")).Raw);
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        await UntilAsync(() => plc.Read(address, "BIT", out double raw) == PlcIoStatus.Ok && raw == 1);
        server.SetWord("DM1", 2);
        await UntilAsync(() => plc.Read(address, "BIT", out double raw) == PlcIoStatus.Ok && raw == 0);
        var write = await plc.TestWriteAsync(address, "BIT", 1);
        Assert.True(write.Ok, write.Message);
        Assert.Equal((ushort)(2 | (1 << bit)), server.GetWord("DM1"));
        Assert.DoesNotContain(server.Commands, c => c.Contains(address + ".", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConstructionAndTemporaryTests_DoNotStartLoop_AndDisposePreventsReconnect()
    {
        await using var server = new FakeHostLinkServer();
        var plc = new HostLinkPlcClient(Settings(server.Port));
        using (plc)
        {
            Assert.Equal(PlcConnectionState.Disconnected, plc.Status.State);
            Assert.Equal(PlcIoStatus.Failed, plc.Read("DM0", "INT16", out _));
            await Task.Delay(80);
            Assert.Empty(server.Commands);
            Assert.True((await plc.TestReadAsync("DM0", "INT16")).Ok);
            Assert.True((await plc.TestWriteAsync("DM1", "INT16", 12)).Ok);
            Assert.Equal(12, server.GetWord("DM1"));
            int count = server.Commands.Count;
            await Task.Delay(100);
            Assert.Equal(count, server.Commands.Count);
            Assert.Equal(PlcConnectionState.Disconnected, plc.Status.State);
            Assert.True((await plc.ConnectAndWaitAsync()).Ok);
            Assert.True((await plc.ConnectAndWaitAsync()).Ok);
            Assert.Equal(3, server.ConnectionCount);
            await UntilAsync(() => plc.Read("DM0", "INT16", out _) == PlcIoStatus.Ok);
        }
        int finalCount = server.Commands.Count;
        await Task.Delay(100);
        Assert.Equal(finalCount, server.Commands.Count);
        Assert.Equal(PlcConnectionState.Disconnected, plc.Status.State);
        Assert.Throws<ObjectDisposedException>(() => plc.Connect());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => plc.TestReadAsync("DM0", "INT16"));
    }

    [Fact]
    public async Task ConnectProbe_RequiresPlcResponse_AndCanceledConnectDoesNotStart()
    {
        await using var server = new FakeHostLinkServer(udp: true);
        var settings = Settings(server.Port, udp: true);
        var probe = await HostLinkPlcClient.TestConnectionAsync(settings);
        Assert.True(probe.Ok, probe.Message);
        Assert.Equal(["?M"], server.Commands.ToArray());
        using var plc = new HostLinkPlcClient(settings);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plc.ConnectAndWaitAsync(cancellation.Token));
        Assert.Equal(PlcConnectionState.Disconnected, plc.Status.State);
        server.DropResponses = true;
        var failed = await plc.ConnectAndWaitAsync();
        Assert.False(failed.Ok);
        Assert.Equal(PlcConnectionState.Faulted, plc.Status.State);
        Assert.Contains("タイムアウト", failed.Message);
    }

    [Fact]
    public async Task BadReadAndWritePoints_AreIsolatedAndRetryAfterCorrection()
    {
        await using var server = new FakeHostLinkServer();
        server.SetWord("DM0", 123);
        server.SetWord("DM1", 456);
        server.RejectAddress("DM1");
        server.RejectAddress("DM11");
        using var plc = new HostLinkPlcClient(Settings(server.Port));
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        plc.Read("DM0", "INT16", out _);
        plc.Read("DM1", "INT16", out _);
        plc.Read("M0", "BIT", out _); // wrong profile, locally rejected
        plc.Write("DM10", "INT16", 77);
        plc.Write("DM11", "INT16", 88);
        await UntilAsync(() => plc.Read("DM0", "INT16", out double raw) == PlcIoStatus.Ok && raw == 123
            && plc.Read("DM1", "INT16", out _) == PlcIoStatus.Failed
            && plc.Read("M0", "BIT", out _) == PlcIoStatus.Failed
            && plc.Write("DM11", "INT16", 88) == PlcIoStatus.Failed
            && server.GetWord("DM10") == 77);
        Assert.Equal(PlcConnectionState.Connected, plc.Status.State);
        Assert.Equal(1, server.ConnectionCount);
        Assert.DoesNotContain("RD M0", server.Commands);
        server.RejectAddress("DM1", false);
        server.RejectAddress("DM11", false);
        await UntilAsync(() => plc.Read("DM1", "INT16", out double raw) == PlcIoStatus.Ok && raw == 456
            && server.GetWord("DM11") == 88);
        Assert.Equal(1, server.ConnectionCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConnectionFailure_ExpiresCacheAndReconnectsWithoutBlockingReads(bool udp)
    {
        await using var server = new FakeHostLinkServer(udp);
        server.SetFloat("DM0", 1.25f);
        using var plc = new HostLinkPlcClient(Settings(server.Port, udp));
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        await UntilAsync(() => plc.Read("DM0", "FLOAT32", out double raw) == PlcIoStatus.Ok && raw == 1.25);
        server.DropResponses = true;
        if (!udp) server.DropConnections();
        await UntilAsync(() => plc.Status.State == PlcConnectionState.Faulted);
        await UntilAsync(() => plc.Read("DM0", "FLOAT32", out _) == PlcIoStatus.Failed);
        var readTime = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++) plc.Read("DM0", "FLOAT32", out _);
        Assert.True(readTime.ElapsedMilliseconds < 100, "演算周期が通信タイムアウトを待っています。");
        server.SetFloat("DM0", 8.5f);
        server.DropResponses = false;
        await UntilAsync(() => plc.Read("DM0", "FLOAT32", out double raw) == PlcIoStatus.Ok && raw == 8.5, 7000);
        Assert.Equal(PlcConnectionState.Connected, plc.Status.State);
        Assert.True(plc.Status.ErrorCount > 0);
        Assert.True(server.Commands.Count(c => c == "?M") >= 2);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RunningTarget_ReadsMvSpAndWritesPv(bool relay)
    {
        await using var server = new FakeHostLinkServer();
        server.SetWord("DM0", 2000);
        server.SetWord("DM2", 2400);
        server.SetBit("MR0", true);
        using var plc = new HostLinkPlcClient(Settings(server.Port));
        using var engine = new SimulationEngine(plc);
        var config = new TargetConfig { Name = "LIC-101", MvAddress = relay ? "MR0" : "DM0", PvAddress = "DM1", SpAddress = "DM2" };
        config.ApplyModelDefaults(ModelCatalog.Get(ModelKind.Level));
        config.MvOnOff = relay;
        var target = ControlTarget.FromConfig(config);
        engine.Add(target);
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        Assert.True(target.Start(engine.Log, out _));
        engine.Run();
        await UntilAsync(() => target.Snapshot().Mv == (relay ? 100 : 50) && target.Snapshot().Sp == 60);
        await UntilAsync(() => server.GetWord("DM1") > 0);
        Assert.Equal(CommStatus.Ok, target.Snapshot().Comm);
        if (relay)
        {
            server.SetBit("MR0", false);
            await UntilAsync(() => target.Snapshot().Mv == 0);
        }
        server.SetWord("DM2", 3200);
        await UntilAsync(() => target.Snapshot().Sp == 80);
    }

    [Fact]
    public async Task LostWriteAcknowledgement_ReconnectsAndSendsLatestPv()
    {
        await using var server = new FakeHostLinkServer();
        using var plc = new HostLinkPlcClient(Settings(server.Port));
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        server.DropResponses = true;
        Assert.Equal(PlcIoStatus.Ok, plc.Write("DM10", "INT16", 20));
        await UntilAsync(() => plc.Status.State == PlcConnectionState.Faulted);
        Assert.Contains("書込み結果が不明", plc.Status.LastError);
        Assert.Equal(20, server.GetWord("DM10")); // PLC received the value, but its acknowledgement was lost.
        server.DropResponses = false;
        await UntilAsync(() => plc.Status.State == PlcConnectionState.Connected);
        Assert.Equal(PlcIoStatus.Ok, plc.Write("DM10", "INT16", 21));
        await UntilAsync(() => server.GetWord("DM10") == 21);
        Assert.True(server.ConnectionCount >= 2);
    }
}
