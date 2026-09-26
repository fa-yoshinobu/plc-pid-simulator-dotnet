using System.Diagnostics;
using PidSimulator.Core;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;
using PidSimulator.Plc.Slmp;

namespace PidSimulator.Tests;

public class Float32Tests
{
    [Fact]
    public void Range_ScalesFractionalRawWithoutRounding()
    {
        var range = new RangeDef { RawMin = -1.25, RawMax = 2.75, EngMin = 0, EngMax = 100 };
        Assert.Equal(0.25, range.ToRaw(37.5));
        Assert.Equal(37.5, range.ToEng(0.25));
        Assert.Equal(-1.25, range.ToRaw(-10));
        Assert.Equal(2.75, range.ToRaw(150));
        Assert.Equal(12.5, new RangeDef().ToRaw(0.3125));
    }

    [Theory]
    [InlineData("melsec:iq-r")]
    [InlineData("melsec:iq-f")]
    public async Task DirectReadWrite_TransfersIeee754FractionalValues(string profile)
    {
        await using var server = new FakeSlmpServer();
        using var plc = new SlmpPlcClient(Settings(server, profile));
        SetFloat(server, 100, -12.375f);
        var read = await plc.TestReadAsync("D100", "FLOAT32");
        Assert.True(read.Ok, read.Message);
        Assert.Equal(-12.375, read.Raw);

        var write = await plc.TestWriteAsync("D102", "FLOAT32", 0.1);
        Assert.True(write.Ok, write.Message);
        Assert.Equal((double)0.1f, write.Raw);
        Assert.Equal(BitConverter.SingleToInt32Bits(0.1f), GetInt32(server, 102));
        Assert.Equal(PlcConnectionState.Disconnected, plc.Status.State);
    }

    [Theory]
    [InlineData("melsec:iq-r")]
    [InlineData("melsec:iq-f")]
    public async Task CachedReadWrite_PreservesFractionsThroughRandomIo(string profile)
    {
        await using var server = new FakeSlmpServer();
        using var plc = new SlmpPlcClient(Settings(server, profile));
        SetFloat(server, 100, 23.625f);
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        Assert.True(await WaitUntil(() => plc.Read("D100", "FLOAT32", out var raw) == PlcIoStatus.Ok && raw == 23.625));
        Assert.Equal(PlcIoStatus.Ok, plc.Write("D102", "FLOAT32", -0.125));
        Assert.True(await WaitUntil(() => GetFloat(server, 102) == -0.125f));

        // Values with the same integer part must still update the cached write.
        Assert.Equal(PlcIoStatus.Ok, plc.Write("D102", "FLOAT32", -0.375));
        Assert.True(await WaitUntil(() => GetFloat(server, 102) == -0.375f));
        Assert.Contains((ushort)0x0403, server.Commands);
        Assert.Contains((ushort)0x1402, server.Commands);
    }

    [Fact]
    public async Task RunningTarget_UsesFractionalMvSpAndWritesFractionalPv()
    {
        await using var server = new FakeSlmpServer();
        using var plc = new SlmpPlcClient(Settings(server));
        SetFloat(server, 100, 12.375f);
        SetFloat(server, 104, 34.625f);
        var config = TargetConfig.Default(ModelKind.Level);
        config.Name = "FLOAT32";
        config.DataType = "FLOAT32";
        config.MvAddress = "D100";
        config.PvAddress = "D102";
        config.SpAddress = "D104";
        config.MvRange.RawMax = config.PvRange.RawMax = config.SpRange.RawMax = 100;
        var target = ControlTarget.FromConfig(config);
        using var engine = new SimulationEngine(plc);
        engine.Add(target);
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        Assert.True(target.Start(engine.Log, out _));
        target.SetForce(ForceKey.Pv, true, 45.875, engine.Log);
        engine.Run();

        Assert.True(await WaitUntil(() => target.Snapshot().Mv == 12.375 && target.Snapshot().Sp == 34.625));
        Assert.True(await WaitUntil(() => GetFloat(server, 102) == 45.875f));
        Assert.Equal(CommStatus.Ok, target.Snapshot().Comm);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    public async Task InvalidFloatWrite_IsRejectedWithoutSending(double raw)
    {
        await using var server = new FakeSlmpServer();
        using var plc = new SlmpPlcClient(Settings(server));
        var write = await plc.TestWriteAsync("D100", "FLOAT32", raw);
        Assert.False(write.Ok);
        Assert.Equal(0, server.ConnectionCount);
        Assert.Empty(server.Commands);
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        Assert.Equal(PlcIoStatus.Failed, plc.Write("D100", "FLOAT32", raw));
        await Task.Delay(100);
        Assert.DoesNotContain((ushort)0x1401, server.Commands);
        Assert.DoesNotContain((ushort)0x1402, server.Commands);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public async Task NonFiniteRead_FailsThatPointAndRecoversWithoutDroppingConnection(float raw)
    {
        await using var server = new FakeSlmpServer();
        using var plc = new SlmpPlcClient(Settings(server));
        SetFloat(server, 100, raw);
        SetFloat(server, 102, 1.625f);
        var probe = await plc.TestReadAsync("D100", "FLOAT32");
        Assert.False(probe.Ok);
        Assert.True((await plc.ConnectAndWaitAsync()).Ok);
        plc.Read("D100", "FLOAT32", out _);
        Assert.True(await WaitUntil(() => plc.Read("D102", "FLOAT32", out var good) == PlcIoStatus.Ok && good == 1.625));
        Assert.Equal(PlcIoStatus.Failed, plc.Read("D100", "FLOAT32", out _));
        Assert.Equal(PlcConnectionState.Connected, plc.Status.State);
        int connections = server.ConnectionCount;
        SetFloat(server, 100, -0.625f);
        Assert.True(await WaitUntil(() => plc.Read("D100", "FLOAT32", out var recovered) == PlcIoStatus.Ok && recovered == -0.625));
        Assert.Equal(connections, server.ConnectionCount);
    }

    [Theory]
    [InlineData("INT16", 12.75, 13)]
    [InlineData("INT16", -12.75, -13)]
    [InlineData("INT16", 70000, short.MaxValue)]
    [InlineData("UINT16", 65534.75, ushort.MaxValue)]
    [InlineData("UINT16", -10, 0)]
    [InlineData("INT32", 123456.75, 123457)]
    [InlineData("INT32", 16777217, 16777217)]
    [InlineData("INT32", -2147483647, -2147483647)]
    [InlineData("INT32", 3e9, int.MaxValue)]
    public async Task IntegerWrite_RoundsAndClampsOnlyAtDeviceEncoding(string dataType, double raw, double expected)
    {
        await using var server = new FakeSlmpServer();
        using var plc = new SlmpPlcClient(Settings(server));
        var write = await plc.TestWriteAsync("D100", dataType, raw);
        Assert.True(write.Ok, write.Message);
        Assert.Equal(expected, write.Raw);
        Assert.Equal(expected, dataType switch
        {
            "UINT16" => (double)unchecked((ushort)server.GetD(100)),
            "INT32" => GetInt32(server, 100),
            _ => server.GetD(100),
        });
        var read = await plc.TestReadAsync("D100", dataType);
        Assert.True(read.Ok, read.Message);
        Assert.Equal(expected, read.Raw);
    }

    [Fact]
    public void DummyPlc_EmulatesConfiguredRawPrecisionAndWordBits()
    {
        using var plc = new DummyPlc();
        Assert.Equal(PlcIoStatus.Ok, plc.Write("D0", "FLOAT32", 0.1));
        Assert.Equal(PlcIoStatus.Ok, plc.Read("D0", "FLOAT32", out var value));
        Assert.Equal((double)0.1f, value);
        Assert.Equal(PlcIoStatus.Ok, plc.Write("D1", "INT16", 2.75));
        Assert.Equal(PlcIoStatus.Ok, plc.Read("D1", "INT16", out value));
        Assert.Equal(3, value);
        Assert.Equal(PlcIoStatus.Ok, plc.Write("D1.2", "BIT", 1));
        Assert.Equal(PlcIoStatus.Ok, plc.Read("D1", "UINT16", out value));
        Assert.Equal(7, value);
        Assert.Equal(PlcIoStatus.Failed, plc.Write("D0", "FLOAT32", double.NaN));
        plc.Read("D0", "FLOAT32", out value);
        Assert.Equal((double)0.1f, value);
    }

    [Theory]
    [InlineData("FLOAT32", -0.125, 0.875, true)]
    [InlineData("FLOAT32", 0, 1e40, false)]
    [InlineData("FLOAT32", 0, double.PositiveInfinity, false)]
    [InlineData("FLOAT32", double.NaN, 1, false)]
    [InlineData("FLOAT32", 1, 1.00000001, false)]
    [InlineData("INT16", 0, 100.5, false)]
    [InlineData("INT16", 0, 40000, false)]
    [InlineData("UINT16", -1, 100, false)]
    [InlineData("INT32", -2147483648d, 2147483647d, true)]
    public void Registration_ValidatesRawTypeAndFiniteRange(string dataType, double min, double max, bool valid)
    {
        var config = TargetConfig.Default(ModelKind.Flow);
        config.Name = "Range";
        config.MvAddress = "D0";
        config.PvAddress = "D10";
        config.UseSp = false;
        config.DataType = dataType;
        config.PvRange.RawMin = min;
        config.PvRange.RawMax = max;
        Assert.Equal(valid, !RegistrationValidator.Check(config, []).Any(r => r.Level == CheckLevel.Block));
    }

    private static PlcSettings Settings(FakeSlmpServer server, string profile = "melsec:iq-r") => new()
    {
        Mode = PlcMode.Slmp, Profile = profile, Host = "127.0.0.1", Port = server.Port, TimeoutMs = 500, CommCycleMs = 20,
    };

    private static void SetFloat(FakeSlmpServer server, uint address, float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        server.SetD(address, unchecked((short)bits));
        server.SetD(address + 1, unchecked((short)(bits >> 16)));
    }

    private static int GetInt32(FakeSlmpServer server, uint address) =>
        unchecked((ushort)server.GetD(address) | (ushort)server.GetD(address + 1) << 16);

    private static float GetFloat(FakeSlmpServer server, uint address) => BitConverter.Int32BitsToSingle(GetInt32(server, address));

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}
