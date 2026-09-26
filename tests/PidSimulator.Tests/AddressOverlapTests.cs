using PidSimulator.Core;
using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public sealed class AddressOverlapTests
{
    private static TargetConfig Config(string type, string mv, string pv, string sp = "D900")
    {
        var config = TargetConfig.Default(ModelKind.Level);
        config.Name = "検査対象";
        config.DataType = type;
        config.MvAddress = mv;
        config.PvAddress = pv;
        config.SpAddress = sp;
        return config;
    }

    [Theory]
    [InlineData("FLOAT32", "D100", "D101")]
    [InlineData("INT32", "D101", "D100")]
    [InlineData("FLOAT32", "W0F", "W10")]
    [InlineData("INT32", "W10", "W0F")]
    [InlineData("FLOAT32", "R9", "R10")]
    [InlineData("FLOAT32", "SD9", "SD10")]
    [InlineData("FLOAT32", "SW0F", "SW10")]
    [InlineData("FLOAT32", "ZR0F", "ZR10")]
    [InlineData("INT16", "D100", "D100")]
    [InlineData("INT16", "d00100", "D100")]
    public void OwnMvAndPv_BlockIntersectingOccupiedWords(string type, string mv, string pv)
    {
        var results = RegistrationValidator.Check(Config(type, mv, pv), []);
        Assert.Contains(results, r => r.Level == CheckLevel.Block && r.Message.Contains("MV"));
    }

    [Theory]
    [InlineData("FLOAT32", "D100", "D102")]
    [InlineData("INT32", "D102", "D100")]
    [InlineData("FLOAT32", "W0F", "W11")]
    [InlineData("FLOAT32", "D100", "W100")]
    [InlineData("INT16", "D100", "D101")]
    [InlineData("UINT16", "W0F", "W10")]
    public void OwnMvAndPv_AllowNonOverlappingWords(string type, string mv, string pv)
    {
        Assert.DoesNotContain(RegistrationValidator.Check(Config(type, mv, pv), []), r => r.Level == CheckLevel.Block);
    }

    [Theory]
    [InlineData("D100", "D101", true)]
    [InlineData("D100", "D102", false)]
    [InlineData("W0F", "W10", true)]
    public void OwnSpAndPv_CheckFullWidth(string pv, string sp, bool overlap)
    {
        bool blocked = RegistrationValidator.Check(Config("FLOAT32", "D500", pv, sp), [])
            .Any(r => r.Level == CheckLevel.Block && r.Message.Contains("SP"));
        Assert.Equal(overlap, blocked);
    }

    [Theory]
    [InlineData("D100.0", "D100", true)]
    [InlineData("D101.15", "D100", true)]
    [InlineData("D101.F", "D100", true)]
    [InlineData("D102.0", "D100", false)]
    [InlineData("W10.A", "W0F", true)]
    [InlineData("M101", "D100", false)]
    [InlineData("Y10", "W0F", false)]
    public void RelayWordBit_ConflictsWithEitherWordOfFloat_NativeBitsAreSeparate(string mv, string pv, bool overlap)
    {
        var config = Config("FLOAT32", mv, pv);
        config.MvOnOff = true;
        bool blocked = RegistrationValidator.Check(config, []).Any(r => r.Level == CheckLevel.Block);
        Assert.Equal(overlap, blocked);
    }

    [Theory]
    [InlineData("FLOAT32", "D100", "INT16", "D101", true)]
    [InlineData("INT16", "D101", "FLOAT32", "D100", true)]
    [InlineData("FLOAT32", "W0F", "UINT16", "W10", true)]
    [InlineData("INT16", "D100", "FLOAT32", "D101", false)]
    [InlineData("FLOAT32", "D100", "INT32", "D102", false)]
    public void CrossTargetOutputOverlap_UsesEachTargetsWidth(string newType, string newPv, string otherType, string otherPv, bool overlap)
    {
        var config = Config(newType, "D500", newPv);
        var other = ControlTarget.FromConfig(Config(otherType, "D600", otherPv, "D800"));
        other.Name = "登録済み";
        var result = RegistrationValidator.Check(config, [other]);
        Assert.Equal(overlap, result.Any(r => r.Level == CheckLevel.Warn && r.Message.Contains("2つの対象")));
        Assert.DoesNotContain(result, r => r.Level == CheckLevel.Block);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossTargetOutputCanOverlapExistingWordOrWordBitInput(bool relay)
    {
        var config = Config("FLOAT32", "D500", "D100");
        var otherConfig = Config("INT16", relay ? "D101.0" : "D101", "D600", "D800");
        otherConfig.MvOnOff = relay;
        var other = ControlTarget.FromConfig(otherConfig);
        other.Name = "登録済み";
        Assert.Contains(RegistrationValidator.Check(config, [other]),
            r => r.Level == CheckLevel.Warn && r.Message.Contains("相手のMV/SPを上書き"));
    }

    [Fact]
    public void NewInputOverlappingExistingOutput_IsAlsoReported()
    {
        var config = Config("INT16", "D101", "D500");
        var other = ControlTarget.FromConfig(Config("FLOAT32", "D600", "D100", "D800"));
        other.Name = "登録済み";
        Assert.Contains(RegistrationValidator.Check(config, [other]),
            r => r.Level == CheckLevel.Warn && r.Message.Contains("相手のPV書込み"));
    }

    [Theory]
    [InlineData("Q100", "Q101")]
    [InlineData("D1A", "D1B")]
    public void UnknownDeviceOrInvalidDecimalNumber_WarnsWithoutInventingOverlap(string mv, string pv)
    {
        var results = RegistrationValidator.Check(Config("FLOAT32", mv, pv), []);
        Assert.DoesNotContain(results, r => r.Level == CheckLevel.Block);
        Assert.Contains(results, r => r.Level == CheckLevel.Warn && r.Message.Contains("使用範囲を確認できません"));
    }

    [Fact]
    public void UnknownButExactlySameAddress_StillBlocks()
    {
        Assert.Contains(RegistrationValidator.Check(Config("FLOAT32", "Q100", "Q100"), []),
            r => r.Level == CheckLevel.Block && r.Message.Contains("同じアドレス"));
    }
}
