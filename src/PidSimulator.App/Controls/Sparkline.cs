using System.Windows;
using System.Windows.Media;

namespace PidSimulator.App.Controls;

public sealed record SparkData(double[] T, double[] Pv, double[] Sp, double Min, double Max, double T0, double T1);

/// <summary>カード用の軽量トレンド（PV実線＋面、SP破線、終点マーカー）</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(SparkData), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush PvBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1D, 0x5C, 0x9E)));
    private static readonly Brush AreaBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x16, 0x1D, 0x5C, 0x9E)));
    private static readonly Pen PvPen = Frozen(new Pen(PvBrush, 1.5) { LineJoin = PenLineJoin.Round });
    private static readonly Pen SpPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x3B, 0x47, 0x55))), 1)
    { DashStyle = new DashStyle([4, 3], 0) });

    public SparkData? Data
    {
        get => (SparkData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var d = Data;
        double w = ActualWidth, h = ActualHeight;
        if (d == null || d.T.Length < 2 || w <= 0 || h <= 0) return;

        double X(double t) => (t - d.T0) / (d.T1 - d.T0) * w;
        double Y(double v) => 6 + (1 - (v - d.Min) / (d.Max - d.Min)) * (h - 12);

        var area = new StreamGeometry();
        using (var c = area.Open())
        {
            c.BeginFigure(new Point(X(d.T[0]), h), true, true);
            for (int i = 0; i < d.T.Length; i++) c.LineTo(new Point(X(d.T[i]), Y(d.Pv[i])), true, false);
            c.LineTo(new Point(X(d.T[^1]), h), true, false);
        }
        area.Freeze();
        dc.DrawGeometry(AreaBrush, null, area);
        dc.DrawGeometry(null, SpPen, Line(d.T, d.Sp, X, Y));
        dc.DrawGeometry(null, PvPen, Line(d.T, d.Pv, X, Y));
        dc.DrawEllipse(PvBrush, null, new Point(Math.Min(X(d.T[^1]), w - 3), Y(d.Pv[^1])), 2.5, 2.5);
    }

    private static StreamGeometry Line(double[] t, double[] v, Func<double, double> x, Func<double, double> y)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x(t[0]), y(v[0])), false, false);
            for (int i = 1; i < t.Length; i++) c.LineTo(new Point(x(t[i]), y(v[i])), true, true);
        }
        g.Freeze();
        return g;
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
