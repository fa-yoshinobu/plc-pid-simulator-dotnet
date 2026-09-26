using PidSimulator.Core;
using PidSimulator.Core.Models;

namespace PidSimulator.Tests;

public sealed class GeometryAndInertiaTests
{
    private static ProcessModel Motor(double inertia = 2)
    {
        var model = ModelCatalog.Create(ModelKind.Motor, 1);
        model.P["motorMode"] = 1;
        model.P["moment"] = inertia;
        model.P["torque"] = 10;
        model.P["load"] = model.P["damping"] = 0;
        model.P["dead"] = model.P["sens"] = model.P["noise"] = 0;
        model.Reset(0);
        return model;
    }

    [Fact]
    public void Motor_AcceleratesByNetTorqueDividedByInertia()
    {
        var model = Motor();
        model.Step(100, 0, 0.1, 0);
        Assert.Equal((10d / 2) * 0.1 * 30 / Math.PI, model.X, 10);
        var heavy = Motor(4);
        heavy.Step(100, 0, 0.1, 0);
        Assert.Equal(model.X / 2, heavy.X, 10);
    }

    [Fact]
    public void Motor_LoadForceAndDisturbanceHaveTorqueUnits()
    {
        var model = Motor();
        model.P["load"] = 80;
        model.InputOverride = 20;
        model.Step(100, 10, 0.1, 0);
        Assert.Equal((10d - 2 - 1) / 2 * 0.1 * 30 / Math.PI, model.X, 10);
    }

    [Fact]
    public void Motor_ViscousResistanceMatchesRotationalEquation()
    {
        var model = Motor();
        model.P["damping"] = 0.5;
        model.Reset(10 * 30 / Math.PI);
        model.Step(100, 0, 0.1, 0);
        // J w' = 10 - 0.5w; w(t) = 20 + (w(0)-20)e^(-0.5t/J).
        double expected = 20 + (10 - 20) * Math.Exp(-0.5 * 0.1 / 2);
        Assert.Equal(expected * 30 / Math.PI, model.X, 10);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1800, 0)]
    [InlineData(900, 25)]
    [InlineData(0, 50)]
    public void Motor_CrossingTorqueLimitIsIndependentOfStepSize(double initial, double mv)
    {
        var largeStep = Motor();
        var smallSteps = Motor();
        foreach (var model in new[] { largeStep, smallSteps })
        {
            model.P["damping"] = 0.01;
            model.P["load"] = 25;
            model.Reset(initial);
        }
        largeStep.Step(mv, 0, 100, 0);
        for (int i = 0; i < 1000; i++) smallSteps.Step(mv, 0, 0.1, i * 0.1);
        Assert.Equal(smallSteps.X, largeStep.X, 7);
        Assert.InRange(largeStep.X, 0, 1800);
    }

    [Fact]
    public void Motor_SteadySpeedBalancesDriveViscousAndLoadTorques()
    {
        var model = Motor();
        model.P["damping"] = 0.01;
        model.P["load"] = 25;
        double command = 1800 * Math.PI / 30 * 0.6;
        double gain = 10 / (1800 * Math.PI / 30 * 0.05);
        double expectedSpeed = (gain * command - 2.5) / (gain + 0.01);
        model.Step(60, 0, 1000, 0);
        Assert.Equal(expectedSpeed * 30 / Math.PI, model.X, 8);
    }

    [Fact]
    public void Motor_LoadCannotTurnStoppedMotorBackwards()
    {
        var model = Motor();
        model.P["load"] = 300;
        model.Step(100, 0, 100, 0);
        Assert.Equal(0, model.X);
        model.Reset(1200);
        model.Step(0, 0, 100, 0);
        Assert.Equal(0, model.X);
    }

    [Fact]
    public void Motor_SimpleResponseRemainsSelectableAndIgnoresInertiaSettings()
    {
        var model = Motor();
        model.P["motorMode"] = 0;
        model.P["tau"] = 0.5;
        model.P["acc"] = 1e6;
        model.P["moment"] = 1e6;
        model.Step(50, 0, 0.1, 0);
        Assert.Equal(900 * (1 - Math.Exp(-0.1 / 0.5)), model.X, 9);
    }

    [Theory]
    [InlineData(ModelKind.Level)]
    [InlineData(ModelKind.LevelPumpIn)]
    [InlineData(ModelKind.LevelPumpOut)]
    [InlineData(ModelKind.LevelDrainValve)]
    public void Tank_DiameterAndEquivalentCrossSectionHaveIdenticalWaterBalance(ModelKind kind)
    {
        var diameter = ModelCatalog.Create(kind, 1);
        var area = ModelCatalog.Create(kind, 1);
        diameter.P["diameter"] = 1600;
        diameter.P["area"] = 999; // Direct diameter wins over an old stored area.
        area.P["area"] = Math.PI * 0.8 * 0.8;
        foreach (var model in new[] { diameter, area })
        {
            model.P["dead"] = model.P["sens"] = model.P["noise"] = 0;
            model.Reset(40);
        }
        for (int i = 0; i < 100; i++)
        {
            diameter.Step(70, 0, 0.1, i * 0.1);
            area.Step(70, 0, 0.1, i * 0.1);
        }
        Assert.Equal(area.X, diameter.X, 10);
    }

    [Fact]
    public void Cylinder_FillRateMatchesVolumeOfAddedWater()
    {
        var model = ModelCatalog.Create(ModelKind.Level);
        model.P["diameter"] = 1000;
        model.P["height"] = 1000;
        model.P["qin"] = 1;
        model.P["kout"] = model.P["valve"] = model.P["dead"] = 0;
        model.Reset(0);
        model.Step(100, 0, 36, 0);
        Assert.Equal(0.01 / (Math.PI / 4) * 100, model.X, 10);
    }

    [Theory]
    [InlineData(ModelKind.Level)]
    [InlineData(ModelKind.LevelPumpIn)]
    [InlineData(ModelKind.LevelPumpOut)]
    [InlineData(ModelKind.LevelDrainValve)]
    public void TankWizard_OffersDiameterAndCapacityAndProducesSameGeometry(ModelKind kind)
    {
        var scenarios = ParameterCalculator.Scenarios(kind);
        var cylinder = scenarios.Single(s => s.Fields.Any(f => f.Key == "diameter"));
        var inputs = cylinder.Fields.ToDictionary(f => f.Key, f => f.Default);
        inputs["diameter"] = 1000;
        inputs["height"] = 2000;
        var cylinderResult = ParameterCalculator.Calculate(kind, cylinder, inputs);
        Assert.Equal(Math.PI / 4, cylinderResult.Values["area"], 12);
        Assert.Equal(1000, cylinderResult.Values["diameter"]);
        var capacity = scenarios.Single(s => s.Fields.Any(f => f.Key == "litres"));
        inputs = capacity.Fields.ToDictionary(f => f.Key, f => f.Default);
        inputs["litres"] = Math.PI / 4 * 2000;
        inputs["height"] = 2000;
        var capacityResult = ParameterCalculator.Calculate(kind, capacity, inputs);
        Assert.Equal(0, capacityResult.Values["diameter"]);
        Assert.Equal(TankGeometry.CapacityCubicMetres(cylinderResult.Values), TankGeometry.CapacityCubicMetres(capacityResult.Values), 12);
    }

    [Fact]
    public void InertiaWizard_UsesDiskGeometryAndPowerTorqueRelation()
    {
        var scenario = ParameterCalculator.Scenarios(ModelKind.Motor).Single(s => s.Fields.Any(f => f.Key == "diskMass"));
        var inputs = scenario.Fields.ToDictionary(f => f.Key, f => f.Default);
        inputs["diskMass"] = 10;
        inputs["diskDiameter"] = 500;
        inputs["motorMoment"] = 0.123;
        var result = ParameterCalculator.Calculate(ModelKind.Motor, scenario, inputs);
        Assert.Equal(0.123 + 10 * 0.25 * 0.25 / 2, result.Values["moment"], 12);
        Assert.Equal(750 / (1800 * Math.PI / 30), result.Values["torque"], 12);
        Assert.Equal(1, result.Values["motorMode"]);
    }

    [Fact]
    public void NewPhysicalParameters_RejectInvalidAndNonFiniteValues()
    {
        Assert.False(ModelCatalog.ValidateParameters(ModelKind.Level, new Dictionary<string, double> { ["diameter"] = 1e-100 }, out _));
        Assert.False(ModelCatalog.ValidateParameter(ModelKind.Motor, "motorMode", 0.5, out _));
        Assert.False(ModelCatalog.ValidateParameter(ModelKind.Motor, "moment", 0, out _));
        Assert.False(ModelCatalog.ValidateParameter(ModelKind.Motor, "torque", double.PositiveInfinity, out _));
    }
}
