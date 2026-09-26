using PidSimulator.Core;
using PidSimulator.Core.Models;
using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public class FlowDriveTests
{
    private static ProcessModel Model(ModelKind kind)
    {
        var model = ModelCatalog.Create(kind, seed: 1);
        foreach (string key in new[] { "dead", "sens", "noise", "tau", "res", "pvar" }) model.P[key] = 0;
        model.P["qmax"] = 60;
        model.Reset(0);
        return model;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(25, 15)]
    [InlineData(50, 30)]
    [InlineData(100, 60)]
    public void Pump_WithoutStaticHead_FlowIsProportionalToSpeed(double speed, double expected)
    {
        var pump = Model(ModelKind.PumpFlow);
        Assert.Equal(expected, pump.Step(speed, 0, 0.1, 0.1), 9);
    }

    [Fact]
    public void Pump_WithStaticHead_MustOvercomeLiftBeforeWaterFlows()
    {
        var pump = Model(ModelKind.PumpFlow);
        pump.P["hmax"] = 40;
        pump.P["hstatic"] = 10;
        // Half speed only develops 40 * 0.5^2 = 10 m: just balances static head.
        Assert.Equal(0, pump.Step(49, 0, 0.1, 0.1), 9);
        Assert.Equal(0, pump.Step(50, 0, 0.1, 0.2), 9);
        Assert.True(pump.Step(51, 0, 0.1, 0.3) > 0);
        // qmax is calibrated at the specified lift and nominal speed.
        Assert.Equal(60, pump.Step(100, 0, 0.1, 0.4), 9);
    }

    [Fact]
    public void Pump_HeadLossAndExtraResistanceReduceFlow()
    {
        var pump = Model(ModelKind.PumpFlow);
        pump.InputOverride = 25;
        Assert.Equal(30, pump.Step(100, 0, 0.1, 0.1), 9);
        pump.InputOverride = 100;
        Assert.Equal(30, pump.Step(100, 75, 0.1, 0.2), 9);
        pump.P["res"] = 300;
        Assert.Equal(30, pump.Step(100, 0, 0.1, 0.3), 9);
        pump.P["hstatic"] = 10;
        Assert.Equal(0, pump.Step(100, 100, 0.1, 0.4), 9);
    }

    [Fact]
    public void Pump_ShortResponseTimeIsStable_AndNoValveCharacteristicIsUsed()
    {
        var pump = Model(ModelKind.PumpFlow);
        Assert.DoesNotContain(ModelCatalog.Get(ModelKind.PumpFlow).Params, p => p.Key == "char");
        pump.P["tau"] = 1e-5;
        Assert.Equal(30, pump.Step(50, 0, 0.1, 0.1), 9);
        Assert.Equal(0, pump.Step(0, 0, 0.1, 0.2), 9);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 9.263225328)]
    [InlineData(2, 42.426406871)]
    public void Valve_KeepsItsExistingOpeningCharacteristics(int characteristic, double expected)
    {
        var valve = Model(ModelKind.Flow);
        valve.P["char"] = characteristic;
        Assert.Equal(expected, valve.Step(50, 0, 0.1, 0.1), 7);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(30, -1, false)]
    [InlineData(30, 30, false)]
    [InlineData(30, 31, false)]
    [InlineData(30, 29, true)]
    [InlineData(30, 0, true)]
    public void Pump_ValidatesHeadParametersTogether(double maximum, double height, bool expected)
    {
        Assert.Equal(expected, ModelCatalog.ValidateParameters(ModelKind.PumpFlow,
            new Dictionary<string, double> { ["hmax"] = maximum, ["hstatic"] = height }, out _));
    }

    [Fact]
    public void Validation_MergesDefaults_AndRejectsInvalidHead()
    {
        Assert.True(ModelCatalog.ValidateParameters(ModelKind.PumpFlow, new Dictionary<string, double>(), out _));
        Assert.False(ModelCatalog.ValidateParameters(ModelKind.PumpFlow,
            new Dictionary<string, double> { ["hstatic"] = 30 }, out _));
        Assert.False(ModelCatalog.ValidateParameters(ModelKind.PumpFlow,
            new Dictionary<string, double> { ["hmax"] = double.NaN }, out _));
    }

    [Fact]
    public void Pump_PersistsItsOwnKindAndHeadParameters()
    {
        var config = TargetConfig.Default(ModelKind.PumpFlow);
        config.Params["hmax"] = 40;
        config.Params["hstatic"] = 12;
        var back = ProjectSerializer.FromJson(ProjectSerializer.ToJson(new ProjectDocument { Targets = [config] }));
        var target = ControlTarget.FromConfig(back.Targets[0]);
        Assert.Equal(ModelKind.PumpFlow, target.Kind);
        Assert.Equal(40, target.GetParam("hmax"));
        Assert.Equal(12, target.GetParam("hstatic"));
    }
}
