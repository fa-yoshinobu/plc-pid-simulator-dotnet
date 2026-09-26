namespace PidSimulator.Core.Models;

/// <summary>縦置きの一定断面積タンク。内径が正なら円の面積、0なら指定した断面積を使う。</summary>
public static class TankGeometry
{
    public static double CrossSectionArea(IReadOnlyDictionary<string, double> parameters)
    {
        double diameter = parameters.GetValueOrDefault("diameter");
        return diameter > 0 ? Math.PI * Math.Pow(diameter / 2000, 2) : parameters["area"];
    }

    public static double CapacityCubicMetres(IReadOnlyDictionary<string, double> parameters) =>
        CrossSectionArea(parameters) * parameters["height"] / 1000;
}
