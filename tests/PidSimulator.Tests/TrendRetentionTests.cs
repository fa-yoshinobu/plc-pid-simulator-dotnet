using PidSimulator.Core;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public class TrendRetentionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(45)]
    [InlineData(240)]
    public void ProjectRoundTrip_PreservesRetention(int minutes)
    {
        var document = new ProjectDocument { Data = new DataSettings { TrendMinutes = minutes } };
        var loaded = ProjectSerializer.FromJson(ProjectSerializer.ToJson(document));
        Assert.Equal(minutes, loaded.Data.TrendMinutes);
    }

    [Fact]
    public void NewOrUnspecifiedRetention_DefaultsToThirtyMinutes()
    {
        Assert.Equal(30, new DataSettings().TrendMinutes);
        Assert.Equal(30, ProjectSerializer.FromJson("{\"FormatVersion\":2}").Data.TrendMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(241)]
    public void InvalidProjectRetention_IsRejectedBeforeApplication(int minutes)
    {
        var document = new ProjectDocument { Data = new DataSettings { TrendMinutes = minutes } };
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.FromJson(ProjectSerializer.ToJson(document)));
    }

    [Fact]
    public void ResizeWrappedBuffer_PreservesNewestSamplesInChronologicalOrder()
    {
        var trend = new TrendBuffer(5);
        for (int i = 1; i <= 8; i++) trend.Add(new TrendSample(i, i, i, i, 0, false));
        trend.Resize(3);
        Assert.Equal(new double[] { 6, 7, 8 }, Times(trend));
        trend.Resize(7);
        Assert.Equal(new double[] { 6, 7, 8 }, Times(trend));
        trend.Add(new TrendSample(9, 9, 9, 9, 0, false));
        Assert.Equal(new double[] { 6, 7, 8, 9 }, Times(trend));
        trend.Clear();
        trend.Resize(2);
        Assert.Empty(Times(trend));
        trend.Add(new TrendSample(10, 0, 0, 0, 0, false));
        Assert.Equal(new double[] { 10 }, Times(trend));
    }

    [Fact]
    public void EngineRetention_AppliesToCurrentFutureAndReplacementTargets()
    {
        using var plc = new DummyPlc();
        using var engine = new SimulationEngine(plc);
        var target = new ControlTarget("A", ModelKind.Flow);
        engine.SetTrendRetention(2);
        engine.Add(target);
        for (int i = 0; i < 650; i++) engine.StepOnce();
        Assert.Equal(650, target.Trend.Count);
        double newest = target.Trend[target.Trend.Count - 1].T;
        engine.SetTrendRetention(1);
        Assert.Equal(600, target.Trend.Count);
        Assert.Equal(newest, target.Trend[599].T);
        Assert.Equal(5.1, target.Trend[0].T, 8);

        var second = new ControlTarget("B", ModelKind.Flow);
        engine.Add(second);
        var replacement = new ControlTarget("C", ModelKind.Heater);
        engine.Replace(second, replacement);
        Assert.All(engine.Targets, t => Assert.Equal(600, t.Trend.Capacity));
        engine.SetTrendRetention(240);
        Assert.All(engine.Targets, t => Assert.Equal(144000, t.Trend.Capacity));
        Assert.Equal(600, target.Trend.Count); // Increasing cannot recover discarded samples.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(241)]
    [InlineData(int.MaxValue)]
    public void InvalidRetention_DoesNotChangeEngineOrBuffer(int minutes)
    {
        using var plc = new DummyPlc();
        using var engine = new SimulationEngine(plc);
        var target = new ControlTarget("A", ModelKind.Flow);
        engine.Add(target);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SetTrendRetention(minutes));
        Assert.Equal(30, engine.TrendMinutes);
        Assert.Equal(18000, target.Trend.Capacity);
    }

    private static double[] Times(TrendBuffer trend)
    {
        var result = new List<TrendSample>();
        trend.CopyRange(double.MinValue, double.MaxValue, result);
        return result.Select(s => s.T).ToArray();
    }
}
