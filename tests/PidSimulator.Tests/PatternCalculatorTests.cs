using PidSimulator.Core;
using PidSimulator.Core.Models;
using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public class PatternCalculatorTests
{
    private static (ControlScenario Scenario, Dictionary<string, double> Inputs) Conditions(ModelKind kind)
    {
        var scenario = ParameterCalculator.Scenarios(kind)[0];
        return (scenario, scenario.Fields.ToDictionary(f => f.Key, f => f.Default));
    }

    private static ProcessModel Bare(ModelKind kind, double initial)
    {
        var model = ModelCatalog.Create(kind, 1);
        model.P["dead"] = model.P["sens"] = model.P["noise"] = 0;
        model.Reset(initial);
        return model;
    }

    [Theory]
    [InlineData(ModelKind.PumpFlow)] [InlineData(ModelKind.LevelPumpIn)] [InlineData(ModelKind.LevelPumpOut)]
    [InlineData(ModelKind.LevelDrainValve)] [InlineData(ModelKind.CoolingWater)]
    [InlineData(ModelKind.PressureSupplyValve)] [InlineData(ModelKind.PressureExhaustValve)] [InlineData(ModelKind.PumpPressure)]
    public void NewPatterns_CalculatedEquipmentSurvivesProjectRoundTrip(ModelKind kind)
    {
        var (scenario, inputs) = Conditions(kind);
        var result = ParameterCalculator.Calculate(kind, scenario, inputs);
        var original = new ControlTarget("pattern", kind);
        Assert.True(original.ApplyCalculatedParameters(result.Values, new EventLog(), out var error), error);
        var saved = new ProjectDocument { Targets = [original.ToConfig()] };
        var back = ProjectSerializer.FromJson(ProjectSerializer.ToJson(saved));
        var restored = ControlTarget.FromConfig(back.Targets[0]);
        Assert.Equal(kind, restored.Kind);
        foreach (var (key, value) in result.Values) Assert.Equal(value, restored.GetParam(key));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2000, 200)]
    public void CoolingWaterWizard_ReachesSpecifiedTemperatureWithContinuousHeatLoads(double heatLoad, double ambientHeat)
    {
        var (scenario, inputs) = Conditions(ModelKind.CoolingWater);
        inputs["amb"] = 30;
        inputs["load"] = heatLoad;
        inputs["loss"] = ambientHeat;
        var result = ParameterCalculator.Calculate(ModelKind.CoolingWater, scenario, inputs);
        var model = Bare(ModelKind.CoolingWater, inputs["initial"]);
        foreach (var (key, value) in result.Values) model.P[key] = value;
        model.P["dead"] = model.P["sens"] = model.P["resp"] = 0;
        model.Step(100, 0, inputs["minutes"] * 60, 0);
        Assert.Equal(inputs["target"], model.X, 7);
    }

    [Fact]
    public void CoolingWaterWizard_RejectsInsufficientFlowAndUnreachableTemperature()
    {
        var (scenario, inputs) = Conditions(ModelKind.CoolingWater);
        inputs["cflow"] = 0.01;
        var ex = Assert.Throws<ArgumentException>(() => ParameterCalculator.Calculate(ModelKind.CoolingWater, scenario, inputs));
        Assert.Contains("冷却水流量", ex.Message);
        inputs["cflow"] = 20;
        inputs["target"] = inputs["tcool"];
        Assert.Throws<ArgumentException>(() => ParameterCalculator.Calculate(ModelKind.CoolingWater, scenario, inputs));
    }

    [Fact]
    public void CoolingWater_NoFlowStopsTransfer_AndWarmerWaterHeatsInstead()
    {
        var model = Bare(ModelKind.CoolingWater, 50);
        model.P["cap"] = 100; model.P["hloss"] = model.P["load"] = model.P["resp"] = 0;
        model.P["ua"] = 1; model.P["cflow"] = 1.2; model.P["tcool"] = 5;
        Assert.Equal(50, model.Step(0, 0, 600, 0), 9);
        model.InputOverride = 80;
        Assert.InRange(model.Step(100, 0, 10, 600), 50.001, 80);
        Assert.Equal(80, model.Step(100, 0, 1e6, 610), 8);
    }

    [Fact]
    public void CoolingWater_EvenUnlimitedAreaIsBoundedByWaterHeatCapacityFlow()
    {
        var model = Bare(ModelKind.CoolingWater, 60);
        model.P["cap"] = 100; model.P["hloss"] = model.P["load"] = model.P["resp"] = 0;
        model.P["ua"] = 1e9; model.P["cflow"] = 1.2; model.P["tcool"] = 10;
        double waterHeatCapacityRate = 20d / 60 * 4.19; // 20 L/min of water.
        Assert.Equal(10 + 50 * Math.Exp(-waterHeatCapacityRate * 10 / 100), model.Step(100, 0, 10, 0), 9);
    }

    [Fact]
    public void DrainValve_ClosedValveFillsByActualInletVolume_AndDisturbanceAddsInflow()
    {
        var model = Bare(ModelKind.LevelDrainValve, 50);
        model.P["area"] = 0.5; model.P["height"] = 1000;
        model.P["qin"] = 3; model.P["valve"] = 0;
        Assert.Equal(60, model.Step(0, 0, 60, 0), 9); // 50 L into 500 L capacity.
        model.InputOverride = 0;
        Assert.Equal(64, model.Step(0, 1.2, 60, 60), 9); // Disturbance adds 20 L.
    }

    [Fact]
    public void DrainValve_OpeningBalancesSquareRootDrainAndCannotMakeLevelNegative()
    {
        var model = Bare(ModelKind.LevelDrainValve, 50);
        model.P["area"] = 0.5; model.P["height"] = 1000;
        model.P["qin"] = 1.5; model.P["kout"] = 3 / Math.Sqrt(50);
        model.P["valve"] = 0; model.P["char"] = 0;
        Assert.Equal(50, model.Step(50, 0, 60, 0), 9); // Half-open drains 25 L/min at 50%.
        Assert.True(model.Step(100, 0, 60, 60) < 50);
        model.P["qin"] = 0;
        Assert.InRange(model.Step(100, 0, 1e8, 120), 0, 0.001);
        model.Reset(0);
        Assert.Equal(0, model.Step(100, 0, 1e8, 0), 9);
    }

    [Fact]
    public void PumpPressureWizard_ReproducesMeasuredRatedOperatingPoint()
    {
        var (scenario, inputs) = Conditions(ModelKind.PumpPressure);
        var result = ParameterCalculator.Calculate(ModelKind.PumpPressure, scenario, inputs);
        var model = Bare(ModelKind.PumpPressure, 0);
        foreach (var (key, value) in result.Values) model.P[key] = value;
        model.P["dead"] = model.P["tau"] = model.P["sens"] = 0;
        Assert.Equal(inputs["pressure"], model.Step(100, 0, 1, 0), 8);
        inputs["pmax"] = inputs["pressure"];
        Assert.Throws<ArgumentException>(() => ParameterCalculator.Calculate(ModelKind.PumpPressure, scenario, inputs));
    }

    [Theory]
    [InlineData(ModelKind.PumpFlow)] [InlineData(ModelKind.LevelPumpIn)] [InlineData(ModelKind.LevelPumpOut)]
    public void PumpWizards_RejectHeadConditionsThatCannotDeliverRatedFlow(ModelKind kind)
    {
        var (scenario, inputs) = Conditions(kind);
        inputs["hmax"] = inputs["hstatic"] = 30;
        Assert.Throws<ArgumentException>(() => ParameterCalculator.Calculate(kind, scenario, inputs));
    }
}
