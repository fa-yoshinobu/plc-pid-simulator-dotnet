using PidSimulator.Core;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public sealed class ModbusAddressTests
{
    private static readonly PlcSettings Settings = new() { Mode = PlcMode.ModbusTcp, Port = 502 };

    [Fact]
    public void Addresses_AreZeroBasedDecimalAndNormalizeWordBitNotation()
    {
        foreach (string address in new[] { "C0", "C65535", "DI65535", "HR0", "IR65535" })
            Assert.True(ModbusAddressRules.TryParse(address, out _), address);
        Assert.True(ModbusAddressRules.TryParse(" hr00001.15 ", out var parsed));
        Assert.Equal("HR1", parsed.BaseAddress);
        Assert.Equal(1, parsed.Number);
        Assert.Equal(15, parsed.BitIndex);
        Assert.Equal(31UL, parsed.StartBit);
        Assert.True(ModbusAddressRules.TryParse("HR1.F", out var hexBit));
        Assert.Equal(parsed, hexBit);
        foreach (string invalid in new[] { "40001", "30001", "HR-1", "HR65536", "IR0A", "C1.0", "DI1.F", "HR1.16", "IR1.0xF", "HR 0" })
            Assert.False(RegistrationValidator.IsAddress(invalid, Settings), invalid);
    }

    [Fact]
    public void SignalTypesAndReadOnlyAreas_AreEnforcedForEveryAccess()
    {
        foreach (string address in new[] { "C0", "DI0", "HR0.0", "IR0.15" })
        {
            Assert.True(PlcBitAddress.IsValid(address, Settings), address);
            Assert.False(ModbusAddressRules.TryResolve(address, "UINT16", false, out _), address);
            Assert.Equal(address == "C0", ModbusAddressRules.TryResolve(address, "BIT", true, out _));
        }
        foreach (string address in new[] { "HR0", "IR0" })
        {
            Assert.False(PlcBitAddress.IsValid(address, Settings), address);
            Assert.True(ModbusAddressRules.TryResolve(address, "INT16", false, out _), address);
            Assert.Equal(address == "HR0", ModbusAddressRules.TryResolve(address, "INT16", true, out _));
        }
        Assert.False(PlcBitAddress.IsValid("HR0.0", new PlcSettings { Mode = PlcMode.Slmp }));
        Assert.False(PlcBitAddress.IsValid("M0", Settings));
        Assert.True(PlcBitAddress.TryWordBit("IR0.10", out var word, out int bit, Settings));
        Assert.Equal("IR0", word);
        Assert.Equal(10, bit);
    }

    [Theory]
    [InlineData("HR", "INT32")]
    [InlineData("HR", "FLOAT32")]
    [InlineData("IR", "INT32")]
    [InlineData("IR", "FLOAT32")]
    public void TwoRegisterValues_MustFitCompletelyWithinAddressSpace(string device, string type)
    {
        Assert.True(ModbusAddressRules.TryResolve(device + "65534", type, false, out _));
        Assert.False(ModbusAddressRules.TryResolve(device + "65535", type, false, out _));
        Assert.True(ModbusAddressRules.TryResolve(device + "65535", "UINT16", false, out _));
        Assert.True(ModbusAddressRules.TryResolve(device + "65535.15", "BIT", false, out _));
    }

    [Theory]
    [InlineData("HR1.15", "HR0", true)]
    [InlineData("HR2.0", "HR0", false)]
    [InlineData("IR1.15", "HR0", false)]
    [InlineData("C0", "HR0", false)]
    [InlineData("DI0", "HR0", false)]
    public void Registration_DetectsWordBitOverlapAcrossFull32BitWidth(string mv, string pv, bool blocked)
    {
        var config = Config(mv, pv);
        config.MvOnOff = true;
        Assert.Equal(blocked, RegistrationValidator.Check(config, [], Settings).Any(r => r.Level == CheckLevel.Block));
    }

    [Theory]
    [InlineData("IR10")]
    [InlineData("DI10")]
    [InlineData("C10")]
    [InlineData("HR10.0")]
    [InlineData("HR65535")]
    public void Registration_BlocksNonNumericOrReadOnlyPvAndAddressOverflow(string pv)
    {
        var results = RegistrationValidator.Check(Config("IR0", pv), [], Settings);
        Assert.Contains(results, r => r.Level == CheckLevel.Block && r.Message.Contains("PV"));
    }

    [Fact]
    public void Registration_WarnsWhenOutputOverlapsAnotherTargetsTwoRegisterInput()
    {
        var config = Config("IR0", "HR101");
        var other = ControlTarget.FromConfig(Config("HR100", "HR500"));
        other.Name = "登録済み";
        var results = RegistrationValidator.Check(config, [other], Settings);
        Assert.DoesNotContain(results, r => r.Level == CheckLevel.Block);
        Assert.Contains(results, r => r.Level == CheckLevel.Warn && r.Message.Contains("相手のMV/SPを上書き"));
    }

    [Fact]
    public void DummyPlc_EmulatesModbusWordBitsWithoutChangingOtherBits()
    {
        using var plc = new DummyPlc();
        plc.Write("HR1", "UINT16", 2);
        plc.Write("HR1.15", "BIT", 1);
        Assert.Equal(PlcIoStatus.Ok, plc.Read("HR1", "UINT16", out double raw));
        Assert.Equal(32770, raw);
        Assert.Equal(PlcIoStatus.Ok, plc.Read("HR1.F", "BIT", out double bit));
        Assert.Equal(1, bit);
        Assert.True(PlcBitAddress.IsValid("DI0", new PlcSettings()));
    }

    private static TargetConfig Config(string mv, string pv)
    {
        var config = TargetConfig.Default(ModelKind.Level);
        config.Name = "Modbus制御対象";
        config.DataType = "FLOAT32";
        config.MvAddress = mv;
        config.PvAddress = pv;
        config.SpAddress = "HR900";
        return config;
    }
}
