using PidSimulator.Core;
using PidSimulator.Core.Models;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public class ParameterCalculatorTests
{
    [Theory]
    [InlineData(ModelKind.Motor)] [InlineData(ModelKind.Flow)] [InlineData(ModelKind.Level)]
    [InlineData(ModelKind.Heater)] [InlineData(ModelKind.Steam)] [InlineData(ModelKind.Chiller)] [InlineData(ModelKind.Pressure)]
    [InlineData(ModelKind.PumpFlow)] [InlineData(ModelKind.LevelPumpIn)] [InlineData(ModelKind.LevelPumpOut)]
    [InlineData(ModelKind.LevelDrainValve)] [InlineData(ModelKind.CoolingWater)]
    [InlineData(ModelKind.PressureSupplyValve)] [InlineData(ModelKind.PressureExhaustValve)] [InlineData(ModelKind.PumpPressure)]
    public void EveryScenario_CalculatesApplicableParameters_WithoutChangingUnrelatedSettings(ModelKind kind)
    {
        foreach (var scenario in ParameterCalculator.Scenarios(kind))
        {
            var result = ParameterCalculator.Calculate(kind, scenario, scenario.Fields.ToDictionary(f => f.Key, f => f.Default));
            var t = new ControlTarget("test", kind);
            double initial = t.InitialPv, sp = t.InternalSp, noise = t.GetParam("noise");
            Assert.True(t.ApplyCalculatedParameters(result.Values, new EventLog(), out _));
            Assert.All(result.Values, p => Assert.Equal(p.Value, t.GetParam(p.Key)));
            Assert.Equal(initial, t.InitialPv); Assert.Equal(sp, t.InternalSp); Assert.Equal(noise, t.GetParam("noise"));
            t.Start(new EventLog(), out _);
            Assert.False(t.ApplyCalculatedParameters(result.Values, new EventLog(), out _));
        }
    }

    [Theory]
    [InlineData(ModelKind.Heater)] [InlineData(ModelKind.Steam)] [InlineData(ModelKind.Chiller)]
    public void ThermalCalculation_MatchesFullPowerTemperatureAfterSpecifiedTime(ModelKind kind)
    {
        var scenario = ParameterCalculator.Scenarios(kind)[0];
        var inputs = scenario.Fields.ToDictionary(f => f.Key, f => f.Default);
        var result = ParameterCalculator.Calculate(kind, scenario, inputs);
        Assert.Equal(30 * 4.19 + 10 * 0.5, result.Values["cap"], 8);
        var model = ModelCatalog.Create(kind, 1);
        foreach (var (key, value) in result.Values) model.P[key] = value;
        model.P["dead"] = 0; model.P["sens"] = 0; model.P["noise"] = 0;
        if (kind == ModelKind.Chiller) model.P["resp"] = 0;
        model.Reset(inputs["initial"]);
        for (int i = 0; i < inputs["minutes"] * 600; i++) model.Step(100, 0, 0.1, i * 0.1);
        Assert.InRange(model.X, inputs["target"] - 0.05, inputs["target"] + 0.05);
    }

    [Fact]
    public void LevelTank_FillsAtSpecifiedLitresPerMinute()
    {
        var model = ModelCatalog.Create(ModelKind.Level);
        model.P["area"] = 0.5; model.P["height"] = 1000; model.P["qin"] = 3;
        model.P["kout"] = 0; model.P["valve"] = 0; model.P["dead"] = 0;
        model.Reset(0);
        for (int i = 0; i < 600; i++) model.Step(100, 0, 0.1, i * 0.1);
        Assert.Equal(10, model.X, 6); // 50 L/min in a 500 L tank, after one minute.
    }

    [Fact]
    public void PressureTank_UsesNormalizedFlowAndSeconds()
    {
        var model = ModelCatalog.Create(ModelKind.Pressure);
        model.P["vol"] = 0.1; model.P["qsup"] = 6; model.P["qout"] = 0;
        model.P["temp"] = 0; // Normal litres are referenced to 0 C and 101.325 kPa.
        model.P["leak"] = 0; model.P["resp"] = 0; model.P["dead"] = 0;
        model.Reset(0);
        for (int i = 0; i < 600; i++) model.Step(100, 0, 0.1, i * 0.1);
        Assert.Equal(101.325, model.X, 6);
    }

    [Fact]
    public void InvalidConditions_AreRejected()
    {
        var scenario = ParameterCalculator.Scenarios(ModelKind.Steam)[0];
        var inputs = scenario.Fields.ToDictionary(f => f.Key, f => f.Default);
        inputs["litres"] = double.NaN;
        Assert.Throws<ArgumentException>(() => ParameterCalculator.Calculate(ModelKind.Steam, scenario, inputs));
        inputs["litres"] = 30; inputs["minutes"] = 0;
        Assert.Throws<ArgumentException>(() => ParameterCalculator.Calculate(ModelKind.Steam, scenario, inputs));
        inputs["minutes"] = 10; inputs["target"] = 10;
        Assert.Throws<ArgumentException>(() => ParameterCalculator.Calculate(ModelKind.Steam, scenario, inputs));
    }

    [Fact]
    public void SteamWizard_RejectsSteamColderThanTarget()
    {
        var scenario = ParameterCalculator.Scenarios(ModelKind.Steam)[0];
        var inputs = scenario.Fields.ToDictionary(f => f.Key, f => f.Default);
        inputs["ts"] = inputs["target"];
        Assert.Throws<ArgumentException>(() => ParameterCalculator.Calculate(ModelKind.Steam, scenario, inputs));
    }

    [Fact]
    public void ChillerWizard_IncludesContinuousHeatLoadAndRespectsCoolingLimit()
    {
        var scenario = ParameterCalculator.Scenarios(ModelKind.Chiller)[0];
        var inputs = scenario.Fields.ToDictionary(f => f.Key, f => f.Default);
        double basePower = ParameterCalculator.Calculate(ModelKind.Chiller, scenario, inputs).Values["cmax"];
        inputs["load"] = 2500;
        var result = ParameterCalculator.Calculate(ModelKind.Chiller, scenario, inputs);
        Assert.Equal(basePower + 2.5, result.Values["cmax"], 8);
        var model = ModelCatalog.Create(ModelKind.Chiller, 1);
        foreach (var (key, value) in result.Values) model.P[key] = value;
        model.P["dead"] = 0; model.P["sens"] = 0; model.P["noise"] = 0; model.P["resp"] = 0;
        model.Reset(inputs["initial"]);
        for (int i = 0; i < inputs["minutes"] * 600; i++) model.Step(100, 0, 0.1, i * 0.1);
        Assert.Equal(inputs["target"], model.X, 6);
        inputs["target"] = 2; inputs["minTemp"] = 3;
        Assert.Throws<ArgumentException>(() => ParameterCalculator.Calculate(ModelKind.Chiller, scenario, inputs));
    }

    [Theory]
    [InlineData("M100")] [InlineData("Y1A")] [InlineData("D1.0")] [InlineData("D1.15")] [InlineData("D1.F")]
    public void RelayMv_MapsBitsToZeroOrFullOutput_AndPersists(string address)
    {
        var cfg = TargetConfig.Default(ModelKind.Heater);
        cfg.Name = "Relay"; cfg.MvOnOff = true; cfg.MvAddress = address; cfg.PvAddress = "D100"; cfg.UseSp = false;
        var doc = ProjectSerializer.FromJson(ProjectSerializer.ToJson(new ProjectDocument { Targets = [cfg] }));
        var t = ControlTarget.FromConfig(doc.Targets[0]);
        Assert.True(t.ToConfig().MvOnOff);
        var plc = new DummyPlc(); using var engine = new SimulationEngine(plc); engine.Add(t);
        Assert.True(t.Start(engine.Log, out _));
        plc.Write(address, "BIT", 1); engine.StepOnce(); Assert.Equal(100, t.Snapshot().Mv);
        plc.Write(address, "BIT", 0); engine.StepOnce(); Assert.Equal(0, t.Snapshot().Mv);
    }

    [Fact]
    public void WordBit_PreservesOtherBits_AndRejectsInvalidIndex()
    {
        var registers = new Dictionary<string, double> { ["D1"] = 2 };
        PlcBitAddress.Write(registers, "D1.0", true); Assert.Equal(3, registers["D1"]);
        PlcBitAddress.Write(registers, "D1.0", false); Assert.Equal(2, registers["D1"]);
        Assert.False(PlcBitAddress.IsValid("D1.16")); Assert.False(PlcBitAddress.IsValid("M1.0"));
    }
}
