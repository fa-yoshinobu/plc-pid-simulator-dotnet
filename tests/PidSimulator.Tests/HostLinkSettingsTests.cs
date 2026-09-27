using PidSimulator.Core;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public sealed class HostLinkSettingsTests
{
    private static PlcSettings Kv(string profile = "keyence:kv-8000") => new() { Mode = PlcMode.HostLink, Profile = profile };

    [Theory]
    [InlineData("keyence:kv-8000", true)]
    [InlineData("keyence:kv-8000-xym", true)]
    [InlineData("keyence:kv-x500", true)]
    [InlineData("keyence:kv-x500-xym", true)]
    [InlineData("keyence:kv-7000", false)]
    [InlineData("keyence:kv-nano", false)]
    public void Simulator_UsesFixedTcpEndpoint_WithoutChangingHardwareSettings(string profile, bool supported)
    {
        var settings = Kv(profile);
        settings.Host = "192.168.0.10";
        settings.Port = 8502;
        settings.Udp = true;
        settings.UseKvSimulator = true;
        settings.UseGxSimulator = true;
        var effective = settings.Effective();
        Assert.Equal(supported, effective.IsKvSimulator);
        Assert.False(effective.UseGxSimulator);
        Assert.Equal(supported ? "127.0.0.1" : "192.168.0.10", effective.Host);
        Assert.Equal(supported ? 8501 : 8502, effective.Port);
        Assert.Equal(!supported, effective.Udp);
        Assert.Equal("192.168.0.10", settings.Host);
        Assert.Equal(8502, settings.Port);

        settings.Mode = PlcMode.Dummy;
        Assert.False(settings.IsKvSimulator);
        Assert.False(settings.IsGxSimulator);
        Assert.Equal("192.168.0.10", settings.Effective().Host);
    }

    [Fact]
    public void Project_RetainsHostLinkSettingsAndAddresses_AndDetectsSimulatorConnectionChange()
    {
        var config = Config("FLOAT32", "DM10.15", "DM20");
        config.MvOnOff = true;
        var document = new ProjectDocument { Plc = Kv(), Targets = [config] };
        document.Plc.UseKvSimulator = true;
        document.Plc.Host = "192.168.0.10";
        document.Plc.Port = 8502;
        document.Plc.Udp = true;
        var restored = ProjectSerializer.FromJson(ProjectSerializer.ToJson(document));
        Assert.True(document.Plc.SameConnection(restored.Plc));
        Assert.True(restored.Plc.IsKvSimulator);
        Assert.Equal("192.168.0.10", restored.Plc.Host);
        Assert.Equal("DM10.15", restored.Targets[0].MvAddress);
        Assert.DoesNotContain(RegistrationValidator.Check(restored.Targets[0], [], restored.Plc), r => r.Level != CheckLevel.Ok);
        restored.Plc.UseKvSimulator = false;
        Assert.False(document.Plc.SameConnection(restored.Plc));
    }

    [Fact]
    public void HostLink_UsesNativeOrXymDeviceNames_AndRejectsWrongSignalTypes()
    {
        foreach (string word in new[] { "DM100", "EM100", "FM100", "ZF100", "WFF", "CM1", "TM1", "VM1" })
            Assert.True(HostLinkAddressRules.TryResolve(word, "FLOAT32", Kv().Profile, out _), word);
        foreach (string bit in new[] { "R100", "MR115", "LR0", "CR15", "BFF", "VBFF", "DM1.15", "DM1.F" })
            Assert.True(PlcBitAddress.IsValid(bit, Kv()), bit);
        foreach (string invalid in new[] { "R116", "MR1A", "DM1.16", "DM1A", "MR100.0", "M100", "D100" })
            Assert.False(RegistrationValidator.IsAddress(invalid, Kv()), invalid);
        foreach (string invalid in new[] { "R100", "DM1.0", "T1", "C1", "Z1" })
            Assert.False(HostLinkAddressRules.TryResolve(invalid, "FLOAT32", Kv().Profile, out _), invalid);

        var xym = Kv("keyence:kv-8000-xym");
        foreach (string word in new[] { "D100", "E100", "F100", "ZF100", "WFF" })
            Assert.True(HostLinkAddressRules.TryResolve(word, "INT32", xym.Profile, out _), word);
        foreach (string bit in new[] { "X19F", "Y10", "M100", "L100", "D1.15", "CR15" })
            Assert.True(PlcBitAddress.IsValid(bit, xym), bit);
        foreach (string invalid in new[] { "X1AF", "M1A", "DM100", "MR100", "R100" })
            Assert.False(RegistrationValidator.IsAddress(invalid, xym), invalid);
        Assert.False(RegistrationValidator.IsAddress("EM1", Kv("keyence:kv-nano")));
        Assert.False(RegistrationValidator.IsAddress("VM1", Kv("keyence:kv-x500")));
    }

    [Fact]
    public void BitIndexAndBankNotation_AreNormalizedWithoutConfusingSlmpRWords()
    {
        Assert.True(HostLinkAddressRules.TryResolve(" dm0001.15 ", "BIT", Kv().Profile, out var wordBit));
        Assert.Equal("DM1", wordBit.BaseAddress);
        Assert.Equal(15, wordBit.BitIndex);
        Assert.Equal(31UL, wordBit.StartBit);
        Assert.True(HostLinkAddressRules.TryResolve("MR115", "BIT", Kv().Profile, out var relay));
        Assert.Equal(31UL, relay.StartBit);
        Assert.True(HostLinkAddressRules.TryResolve("X19F", "BIT", Kv("keyence:kv-8000-xym").Profile, out var input));
        Assert.Equal(319, input.Number);
        Assert.Equal("X19F", input.BaseAddress);
        Assert.True(PlcBitAddress.IsValid("R1.0", new PlcSettings { Mode = PlcMode.Slmp }));
        Assert.False(PlcBitAddress.IsValid("R1.0", Kv()));
        Assert.False(PlcBitAddress.IsValid("DM1.0", new PlcSettings { Mode = PlcMode.Slmp }));
    }

    [Theory]
    [InlineData("DM101.15", "DM100", true)]
    [InlineData("DM102.0", "DM100", false)]
    [InlineData("W10.F", "W0F", true)]
    [InlineData("MR100", "DM100", false)]
    public void RelayRegistration_DetectsOverlapWithBothWordsOfFloat(string mv, string pv, bool overlap)
    {
        var config = Config("FLOAT32", mv, pv);
        config.MvOnOff = true;
        Assert.Equal(overlap, RegistrationValidator.Check(config, [], Kv()).Any(r => r.Level == CheckLevel.Block));
    }

    [Fact]
    public void NumericRegistration_DetectsAdjacentAndCrossTargetWrites()
    {
        var config = Config("INT32", "DM100", "DM101");
        Assert.Contains(RegistrationValidator.Check(config, [], Kv()), r => r.Level == CheckLevel.Block && r.Message.Contains("重複"));
        config.MvAddress = "DM500";
        var other = ControlTarget.FromConfig(Config("FLOAT32", "DM700", "DM100"));
        Assert.Contains(RegistrationValidator.Check(config, [other], Kv()), r => r.Level == CheckLevel.Warn && r.Message.Contains("2つの対象"));
        config.PvAddress = "MR100";
        Assert.Contains(RegistrationValidator.Check(config, [], Kv()), r => r.Level == CheckLevel.Block && r.Message.Contains("データ型"));
    }

    [Fact]
    public void DummyPlc_CanExerciseHostLinkWordBits_WithoutChangingOtherBits()
    {
        using var plc = new DummyPlc();
        Assert.Equal(PlcIoStatus.Ok, plc.Write("DM1", "UINT16", 2));
        Assert.Equal(PlcIoStatus.Ok, plc.Write("DM1.15", "BIT", 1));
        Assert.Equal(PlcIoStatus.Ok, plc.Read("DM1", "UINT16", out double raw));
        Assert.Equal(32770, raw);
        Assert.Equal(PlcIoStatus.Ok, plc.Read("DM1.F", "BIT", out double bit));
        Assert.Equal(1, bit);
        plc.Write("DM1.15", "BIT", 0);
        plc.Read("DM1", "UINT16", out raw);
        Assert.Equal(2, raw);
    }

    private static TargetConfig Config(string type, string mv, string pv)
    {
        var config = TargetConfig.Default(ModelKind.Level);
        config.Name = "KEYENCE制御対象";
        config.MvAddress = mv;
        config.PvAddress = pv;
        config.SpAddress = "DM900";
        config.DataType = type;
        return config;
    }
}
