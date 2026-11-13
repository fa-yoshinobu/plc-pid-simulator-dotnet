using System.Diagnostics;
using PidSimulator.Core;
using PidSimulator.Core.Models;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;
using PidSimulator.Plc.Modbus;

namespace PidSimulator.Tests;

/// <summary>実ソケットと独立したMBAPテスト機器を用い、型変換・通信復旧・Modbus機能コードを検証する。</summary>
public sealed class ModbusIntegrationTests
{
    private static PlcSettings Settings(int port, ModbusWordOrder order = ModbusWordOrder.HighWordFirst) => new()
    {
        Mode = PlcMode.ModbusTcp, Host = "127.0.0.1", Port = port, ModbusUnitId = 1,
        ModbusWordOrder = order, ModbusTestAddress = "IR0", TimeoutMs = 400, CommCycleMs = 30,
    };

    private static async Task UntilAsync(Func<bool> condition, int timeout = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeout)
        {
            if (condition()) return;
            await Task.Delay(15);
        }
        Assert.True(condition(), "Modbusの通信結果が制限時間内に反映されませんでした。");
    }

    [Theory]
    [InlineData(ModbusWordOrder.HighWordFirst)]
    [InlineData(ModbusWordOrder.LowWordFirst)]
    public async Task NumericTypes_KeepExactValuesWithBothWordOrders_AndAcceptFragmentedResponses(ModbusWordOrder order)
    {
        await using var server = new FakeModbusServer { SplitResponses = true };
        bool high = order == ModbusWordOrder.HighWordFirst;
        server.SetWord("IR", 0, 0x8000);
        server.SetWord("IR", 1, 65535);
        server.SetWord("IR", 2, high ? (ushort)0x7FFF : (ushort)0xFFFF);
        server.SetWord("IR", 3, high ? (ushort)0xFFFF : (ushort)0x7FFF);
        server.SetWord("IR", 4, high ? (ushort)0x3FA0 : (ushort)0);
        server.SetWord("IR", 5, high ? (ushort)0 : (ushort)0x3FA0); // FLOAT32 1.25
        var points = new[]
        {
            (Address: (ushort)0, Type: "INT16", Value: -32768d),
            (Address: (ushort)1, Type: "UINT16", Value: 65535d),
            (Address: (ushort)2, Type: "INT32", Value: 2147483647d),
            (Address: (ushort)4, Type: "FLOAT32", Value: 1.25d),
        };
        using var plc = new ModbusPlcClient(Settings(server.Port, order));
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        foreach (var point in points)
        {
            var read = await plc.TestReadAsync($"IR{point.Address}", point.Type);
            Assert.True(read.Ok, read.Message);
            Assert.Equal(point.Value, read.Raw);
            var write = await plc.TestWriteAsync($"HR{point.Address}", point.Type, point.Value);
            Assert.True(write.Ok, write.Message);
            var reread = await plc.TestReadAsync($"HR{point.Address}", point.Type);
            Assert.True(reread.Ok, reread.Message);
            Assert.Equal(point.Value, reread.Raw);
        }
        Assert.Equal((ushort)0x8000, server.GetWord("HR", 0));
        Assert.Equal((ushort)65535, server.GetWord("HR", 1));
        Assert.Equal(high ? (ushort)0x7FFF : (ushort)0xFFFF, server.GetWord("HR", 2));
        Assert.Equal(high ? (ushort)0x3FA0 : (ushort)0, server.GetWord("HR", 4));
        Assert.Equal(high ? (ushort)0 : (ushort)0x3FA0, server.GetWord("HR", 5));
        Assert.Contains(server.Requests, r => r.Function == 6 && r.Address == 0 && r.Count == 1 && r.Values.SequenceEqual(new ushort[] { 0x8000 }));
        Assert.Contains(server.Requests, r => r.Function == 16 && r.Address == 4 && r.Count == 2);
        Assert.DoesNotContain(server.Requests, r => r.Function == 6 && r.Address is 4 or 5);

        foreach (var point in points)
            await UntilAsync(() => plc.Read($"IR{point.Address}", point.Type, out double raw) == PlcIoStatus.Ok && raw == point.Value);
        Assert.Equal(PlcIoStatus.Ok, plc.Write("HR4", "FLOAT32", 2.5));
        await UntilAsync(() => server.GetWord("HR", high ? (ushort)4 : (ushort)5) == 0x4020);
        Assert.False((await plc.TestWriteAsync("HR4", "FLOAT32", double.PositiveInfinity)).Ok);
        Assert.Equal(PlcIoStatus.Failed, plc.Write("HR4", "FLOAT32", double.MaxValue));
    }

    [Theory]
    [InlineData("C0", "C", 0, 1)]
    [InlineData("DI7", "DI", 7, 2)]
    [InlineData("HR8.0", "HR", 8, 3)]
    [InlineData("IR8.F", "IR", 8, 4)]
    public async Task RelayAndRegisterBits_ReadOnlyTheirSelectedBit(string address, string area, int number, int function)
    {
        await using var server = new FakeModbusServer();
        server.SetWord(area, (ushort)number, area is "C" or "DI" ? (ushort)1 : (ushort)0x8001);
        using var plc = new ModbusPlcClient(Settings(server.Port));
        var read = await plc.TestReadAsync(address, "BIT");
        Assert.True(read.Ok, read.Message);
        Assert.Equal(1, read.Raw);
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        await UntilAsync(() => plc.Read(address, "BIT", out double raw) == PlcIoStatus.Ok && raw == 1);
        server.SetWord(area, (ushort)number, area is "C" or "DI" ? (ushort)0 : (ushort)2);
        await UntilAsync(() => plc.Read(address, "BIT", out double raw) == PlcIoStatus.Ok && raw == 0);
        Assert.Contains(server.Requests, r => r.Function == function && r.Address == number && r.Count == 1);
    }

    [Theory]
    [InlineData(0, "C0", 1)]
    [InlineData(1, "DI0", 2)]
    [InlineData(255, "HR6.0", 3)]
    public async Task ConnectionProbe_UsesConfiguredAreaAndUnitId(int unitId, string address, int function)
    {
        await using var server = new FakeModbusServer();
        var settings = Settings(server.Port);
        settings.ModbusUnitId = unitId;
        settings.ModbusTestAddress = address;
        var test = await ModbusPlcClient.TestConnectionAsync(settings);
        Assert.True(test.Ok, test.Message);
        var request = Assert.Single(server.Requests);
        Assert.Equal(unitId, request.UnitId);
        Assert.Equal(function, request.Function);
        Assert.Equal(address == "HR6.0" ? 6 : 0, request.Address);
        await UntilAsync(() => server.ActiveConnections == 0);
    }

    [Theory]
    [InlineData("IR0", "UINT16")]
    [InlineData("DI0", "BIT")]
    [InlineData("HR0.0", "BIT")]
    [InlineData("C0", "INT16")]
    [InlineData("HR65535", "FLOAT32")]
    public async Task InvalidWrite_IsRejectedWithoutSendingAWrite(string address, string type)
    {
        await using var server = new FakeModbusServer();
        using var plc = new ModbusPlcClient(Settings(server.Port));
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        var result = await plc.TestWriteAsync(address, type, 1);
        Assert.False(result.Ok);
        Assert.DoesNotContain(server.Requests, r => r.Function is 5 or 6 or 16);
        Assert.True((await plc.TestReadAsync("IR0", "UINT16")).Ok);
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task CoilWrite_UsesExplicitOnOffEncoding()
    {
        await using var server = new FakeModbusServer();
        using var plc = new ModbusPlcClient(Settings(server.Port));
        Assert.True((await plc.TestWriteAsync("C3", "BIT", 1)).Ok);
        Assert.Equal(1, server.GetWord("C", 3));
        Assert.True((await plc.TestWriteAsync("C3", "BIT", 0)).Ok);
        Assert.Equal(0, server.GetWord("C", 3));
        var writes = server.Requests.Where(r => r.Function == 5).ToArray();
        Assert.Equal(new ushort[] { 0xFF00, 0 }, writes.Select(r => r.Values.Single()).ToArray());
    }

    [Fact]
    public async Task SettingsAndTemporaryTests_DoNotStartCommunication_AndDisposeStopsTheLoop()
    {
        await using var server = new FakeModbusServer();
        var plc = new ModbusPlcClient(Settings(server.Port));
        using (plc)
        {
            Assert.Equal(PlcConnectionState.Disconnected, plc.Status.State);
            Assert.Equal(PlcIoStatus.Failed, plc.Read("IR0", "UINT16", out _));
            await Task.Delay(80);
            Assert.Empty(server.Requests);
            Assert.True((await plc.TestReadAsync("IR0", "UINT16")).Ok);
            Assert.True((await plc.TestWriteAsync("HR6", "UINT16", 42)).Ok);
            int count = server.Requests.Count;
            await Task.Delay(100);
            Assert.Equal(count, server.Requests.Count);
            Assert.Equal(PlcConnectionState.Disconnected, plc.Status.State);
            await UntilAsync(() => server.ActiveConnections == 0);
            Assert.True((await plc.ConnectAndWaitAsync()).Ok);
            await UntilAsync(() => plc.Read("IR0", "UINT16", out _) == PlcIoStatus.Ok);
        }
        await UntilAsync(() => server.ActiveConnections == 0);
        int final = server.Requests.Count;
        await Task.Delay(100);
        Assert.Equal(final, server.Requests.Count);
        Assert.Throws<ObjectDisposedException>(() => plc.Connect());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => plc.TestReadAsync("IR0", "UINT16"));
    }

    [Fact]
    public async Task DeviceExceptions_AffectOnlyBadPoints_AndRetryAfterCorrection()
    {
        await using var server = new FakeModbusServer();
        server.SetWord("IR", 0, 123);
        server.SetWord("IR", 1, 456);
        using var plc = new ModbusPlcClient(Settings(server.Port));
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        server.RejectAddress("IR", 1);
        server.RejectAddress("HR", 7);
        plc.Read("IR0", "UINT16", out _);
        plc.Read("IR1", "UINT16", out _);
        plc.Write("HR6", "UINT16", 10);
        plc.Write("HR7", "UINT16", 20);
        await UntilAsync(() => plc.Read("IR0", "UINT16", out double raw) == PlcIoStatus.Ok && raw == 123
            && plc.Read("IR1", "UINT16", out _) == PlcIoStatus.Failed
            && plc.Write("HR7", "UINT16", 20) == PlcIoStatus.Failed && server.GetWord("HR", 6) == 10);
        Assert.Contains("0x02", plc.Status.LastError);
        Assert.Equal(PlcConnectionState.Connected, plc.Status.State);
        Assert.Equal(1, server.ConnectionCount);
        server.RejectAddress("IR", 1, false);
        server.RejectAddress("HR", 7, false);
        await UntilAsync(() => plc.Read("IR1", "UINT16", out double raw) == PlcIoStatus.Ok && raw == 456 && server.GetWord("HR", 7) == 20);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ManualReadTimeoutOrCancellation_RetiresIdleSessionAndReconnects(bool cancel)
    {
        await using var server = new FakeModbusServer();
        using var plc = new ModbusPlcClient(Settings(server.Port));
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        server.DropResponses = true;
        using var cts = new CancellationTokenSource();
        int before = server.Requests.Count;
        Task<PlcTestResult> read = plc.TestReadAsync("IR1", "UINT16", cts.Token);
        await UntilAsync(() => server.Requests.Count > before);
        if (cancel)
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        }
        else
        {
            var failed = await read;
            Assert.False(failed.Ok);
            Assert.Contains("タイムアウト", failed.Message);
        }
        server.DropResponses = false;
        server.SetWord("IR", 1, 321);
        await UntilAsync(() => server.ConnectionCount >= 2 && plc.Status.State == PlcConnectionState.Connected);
        var success = await plc.TestReadAsync("IR1", "UINT16");
        Assert.True(success.Ok, success.Message);
        Assert.Equal(321, success.Raw);
    }

    [Fact]
    public async Task BackgroundTimeout_ExpiresCacheAndReadsRemainNonBlocking()
    {
        await using var server = new FakeModbusServer();
        server.SetWord("IR", 0, 10);
        using var plc = new ModbusPlcClient(Settings(server.Port));
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        await UntilAsync(() => plc.Read("IR0", "UINT16", out double raw) == PlcIoStatus.Ok && raw == 10);
        server.DropResponses = true;
        await UntilAsync(() => plc.Read("IR0", "UINT16", out _) == PlcIoStatus.Failed);
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++) plc.Read("IR0", "UINT16", out _);
        Assert.True(timer.ElapsedMilliseconds < 100);
        server.SetWord("IR", 0, 20);
        server.DropResponses = false;
        await UntilAsync(() => plc.Read("IR0", "UINT16", out double raw) == PlcIoStatus.Ok && raw == 20);
        Assert.True(server.ConnectionCount >= 2);
    }

    [Fact]
    public async Task WrongTransactionAndWriteEcho_AreNotAcceptedAsSuccess()
    {
        await using var server = new FakeModbusServer();
        using var plc = new ModbusPlcClient(Settings(server.Port));
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        server.WrongTransactionId = true;
        Assert.False((await plc.TestReadAsync("IR0", "UINT16")).Ok);
        server.WrongTransactionId = false;
        await UntilAsync(() => server.ConnectionCount >= 2 && plc.Status.State == PlcConnectionState.Connected);
        server.WrongWriteEcho = true;
        var write = await plc.TestWriteAsync("HR6", "UINT16", 100);
        Assert.False(write.Ok);
        Assert.Contains("書込み結果が不明", write.Message);
        server.WrongWriteEcho = false;
        await UntilAsync(() => server.ConnectionCount >= 3 && plc.Status.State == PlcConnectionState.Connected);
        Assert.True((await plc.TestWriteAsync("HR6", "UINT16", 101)).Ok);
    }

    [Fact]
    public async Task DelayedWriteAcknowledgement_RetiresSessionAndSendsLatestPv()
    {
        await using var server = new FakeModbusServer();
        using var plc = new ModbusPlcClient(Settings(server.Port));
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        server.DelayMs = 650;
        var write = await plc.TestWriteAsync("HR6", "UINT16", 100);
        Assert.False(write.Ok);
        Assert.Contains("書込み結果が不明", write.Message);
        Assert.Equal(100, server.GetWord("HR", 6));
        server.DelayMs = 0;
        await UntilAsync(() => server.ConnectionCount >= 2 && plc.Status.State == PlcConnectionState.Connected);
        Assert.Equal(PlcIoStatus.Ok, plc.Write("HR6", "UINT16", 101));
        await UntilAsync(() => server.GetWord("HR", 6) == 101);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EthIoRegisterMap_RunsAiOrDiMvAndAoPv(bool relay)
    {
        await using var server = new FakeModbusServer { EthIoMapOnly = true };
        server.SetWord("IR", 0, 2048);
        server.SetBit("DI", 0, true);
        var config = TargetConfig.Default(ModelKind.Level);
        config.Name = "LIC-101";
        config.DataType = "UINT16";
        config.MvAddress = relay ? "DI0" : "IR0";
        config.MvOnOff = relay;
        config.PvAddress = "HR6";
        config.UseSp = false;
        config.MvRange.RawMin = config.PvRange.RawMin = config.SpRange.RawMin = 0;
        config.MvRange.RawMax = config.PvRange.RawMax = config.SpRange.RawMax = 4095;
        using var plc = new ModbusPlcClient(Settings(server.Port));
        using var engine = new SimulationEngine(plc);
        var target = ControlTarget.FromConfig(config);
        engine.Add(target);
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        Assert.True(target.Start(engine.Log, out _));
        engine.Run();
        await UntilAsync(() => Math.Abs(target.Snapshot().Mv - (relay ? 100 : 2048d / 4095 * 100)) < 0.01);
        await UntilAsync(() => server.GetWord("HR", 6) > 0);
        Assert.InRange(server.GetWord("HR", 6), (ushort)1, (ushort)4095);
        Assert.Equal(CommStatus.Ok, target.Snapshot().Comm);
        Assert.Contains(server.Requests, r => r.Function == 6 && r.Address == 6);
        Assert.DoesNotContain(server.Requests, r => r.Function is 5 or 16);
        if (relay)
        {
            server.SetBit("DI", 0, false);
            await UntilAsync(() => target.Snapshot().Mv == 0);
        }
    }
}
