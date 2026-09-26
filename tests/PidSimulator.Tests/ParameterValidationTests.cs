using PidSimulator.Core;
using PidSimulator.Core.Models;
using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public class ParameterValidationTests
{
    [Fact]
    public void CatalogDefaults_AreValid()
    {
        foreach (var info in ModelCatalog.All)
        {
            foreach (var def in info.Params)
                Assert.True(ModelCatalog.ValidateParameter(info.Kind, def.Key, def.Default, out _), def.Key);
        }
    }

    [Theory]
    [InlineData(ModelKind.Heater, "cap", 0)]
    [InlineData(ModelKind.Level, "area", -1)]
    [InlineData(ModelKind.Pressure, "vol", 0)]
    [InlineData(ModelKind.Flow, "tau", -1)]
    [InlineData(ModelKind.Steam, "char", 1.5)]
    [InlineData(ModelKind.Steam, "dead", -1)]
    [InlineData(ModelKind.Pressure, "temp", -274)]
    public void NonPhysicalParameters_AreRejectedByEditingAndLoading(ModelKind kind, string key, double value)
    {
        var target = new ControlTarget("test", kind);
        double before = target.GetParam(key);
        Assert.False(target.SetParam(key, value, new EventLog(), out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(before, target.GetParam(key));
        var config = target.ToConfig();
        config.Params[key] = value;
        Assert.Throws<InvalidDataException>(() => ControlTarget.FromConfig(config));
        var json = ProjectSerializer.ToJson(new ProjectDocument { Targets = [config] });
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.FromJson(json));
    }

    [Fact]
    public void InvalidBatch_LeavesAllParametersUnchanged()
    {
        var target = new ControlTarget("test", ModelKind.Heater);
        var before = target.ToConfig().Params;
        Assert.False(target.ApplyCalculatedParameters(new Dictionary<string, double>
            { ["pmax"] = 123, ["cap"] = 0 }, new EventLog(), out _));
        Assert.Equal(before, target.ToConfig().Params);
    }

    [Fact]
    public void UnknownParameters_AreRejectedOnLoadAndBeforeApplyingSettings()
    {
        var target = new ControlTarget("motor", ModelKind.Motor);
        var before = target.ToConfig();
        var config = target.ToConfig();
        config.Name = "changed";
        config.Params["nmax"] = 1200;
        config.Params["inertia"] = 1.2;

        Assert.Throws<InvalidDataException>(() => ControlTarget.FromConfig(config));
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.FromJson(
            ProjectSerializer.ToJson(new ProjectDocument { Targets = [config] })));
        Assert.Throws<InvalidDataException>(() => target.ApplySettings(config));
        Assert.Equal(before.Name, target.Name);
        Assert.Equal(before.Params, target.ToConfig().Params);
    }

    [Fact]
    public void HeadPair_IsValidatedAndAppliedAtomically()
    {
        var target = new ControlTarget("pump", ModelKind.PumpFlow);
        var log = new EventLog();
        Assert.True(target.SetParam("hstatic", 20, log, out _));
        Assert.False(target.SetParam("hmax", 10, log, out _));
        Assert.Equal(30, target.GetParam("hmax"));
        // Neither edit order may cause a partly applied, physically invalid pair.
        Assert.True(target.ApplyCalculatedParameters(new Dictionary<string, double>
            { ["hmax"] = 10, ["hstatic"] = 5 }, log, out _));
        Assert.Equal(10, target.GetParam("hmax"));
        Assert.Equal(5, target.GetParam("hstatic"));
        var config = target.ToConfig();
        config.Params["hstatic"] = 20;
        Assert.Throws<InvalidDataException>(() => ControlTarget.FromConfig(config));
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.FromJson(
            ProjectSerializer.ToJson(new ProjectDocument { Targets = [config] })));
    }
}
