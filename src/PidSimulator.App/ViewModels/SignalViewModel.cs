using CommunityToolkit.Mvvm.ComponentModel;
using PidSimulator.Core;

namespace PidSimulator.App.ViewModels;

/// <summary>SP / PV / MV の1信号分の表示値</summary>
public sealed partial class SignalViewModel(string key, string label, RangeDef range, int decimals) : ObservableObject
{
    private readonly string _format = "F" + decimals;

    public string Key { get; } = key;
    public string ShortLabel { get; } = key.ToUpperInvariant();
    public string Label { get; } = label;
    public string Unit => range.Unit;
    public string MinText => range.EngMin.ToString("G");
    public string MaxText => range.EngMax.ToString("G");
    public string RawRangeText => $"({range.RawMin}–{range.RawMax})";

    [ObservableProperty] private string _valueText = "—";
    [ObservableProperty] private string _rawText = "—";
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private bool _isForced;
    [ObservableProperty] private string _source = "";

    public void Update(double value, double raw, bool forced)
    {
        ValueText = double.IsFinite(value) ? value.ToString(_format) : "—";
        RawText = double.IsFinite(raw) ? raw.ToString("G9") : "—";
        Fraction = double.IsFinite(value) ? range.Fraction(value) : 0;
        IsForced = forced;
    }
}
