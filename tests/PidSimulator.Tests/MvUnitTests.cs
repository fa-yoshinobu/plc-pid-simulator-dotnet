using PidSimulator.Core;
using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public class MvUnitTests
{
    [Theory]
    [InlineData(ModelKind.Flow, "Hz", false)]
    [InlineData(ModelKind.Level, "rpm", false)]
    [InlineData(ModelKind.Steam, "kW", false)]
    [InlineData(ModelKind.CoolingWater, "kW", false)]
    [InlineData(ModelKind.PressureSupplyValve, "Hz", false)]
    [InlineData(ModelKind.Motor, "kW", false)]
    [InlineData(ModelKind.Heater, "Hz", false)]
    [InlineData(ModelKind.Motor, "Hz", true)]
    [InlineData(ModelKind.Heater, "kW", true)]
    [InlineData(ModelKind.Flow, "V", true)]
    public void UnsupportedActuatorUnit_IsUnavailableAndRejectedByRegistrationAndProjectLoad(ModelKind kind, string unit, bool relay)
    {
        var config = Config(kind, unit, relay);
        Assert.DoesNotContain(unit, EngineeringUnits.GetMvUnits(kind, relay));
        Assert.Contains(RegistrationValidator.Check(config, []),
            r => r.Level == CheckLevel.Block && r.Message.Contains("MV単位"));

        string json = ProjectSerializer.ToJson(new ProjectDocument { Targets = [config] });
        var error = Assert.Throws<InvalidDataException>(() => ProjectSerializer.FromJson(json));
        Assert.Contains("MV単位", error.Message);
    }

    [Theory]
    [InlineData(ModelKind.Flow, "V", 0, 10, false)]
    [InlineData(ModelKind.Steam, "mA", 4, 20, false)]
    [InlineData(ModelKind.Motor, "rpm", 0, 1800, false)]
    [InlineData(ModelKind.PumpFlow, "Hz", 0, 60, false)]
    [InlineData(ModelKind.Pressure, "Hz", 0, 60, false)]
    [InlineData(ModelKind.Heater, "kW", 0, 10, false)]
    [InlineData(ModelKind.Chiller, "kW", 0, 12, false)]
    [InlineData(ModelKind.Motor, "%", 0, 100, true)]
    public void SupportedActuatorCommand_PassesRegistrationAndSurvivesProjectRoundtrip(ModelKind kind, string unit, double min, double max, bool relay)
    {
        var config = Config(kind, unit, relay);
        config.MvRange.EngMin = min;
        config.MvRange.EngMax = max;
        Assert.Contains(unit, EngineeringUnits.GetMvUnits(kind, relay));
        Assert.DoesNotContain(RegistrationValidator.Check(config, []), r => r.Level == CheckLevel.Block);

        var document = ProjectSerializer.FromJson(ProjectSerializer.ToJson(new ProjectDocument { Targets = [config] }));
        var restored = Assert.Single(document.Targets);
        Assert.Equal(kind, restored.Kind);
        Assert.Equal(relay, restored.MvOnOff);
        Assert.Equal(unit, restored.MvRange.Unit);
        Assert.Equal(min, restored.MvRange.EngMin);
        Assert.Equal(max, restored.MvRange.EngMax);
        Assert.DoesNotContain(RegistrationValidator.Check(restored, []), r => r.Level == CheckLevel.Block);
    }

    private static TargetConfig Config(ModelKind kind, string unit, bool relay)
    {
        var config = TargetConfig.Default(kind);
        config.Name = "MV unit regression";
        config.MvOnOff = relay;
        config.MvAddress = relay ? "M100" : "D1000";
        config.PvAddress = "D1002";
        config.SpAddress = "D1004";
        config.MvRange.Unit = unit;
        return config;
    }
}
