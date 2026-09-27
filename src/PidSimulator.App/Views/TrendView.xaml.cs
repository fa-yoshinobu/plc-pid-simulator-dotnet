using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PidSimulator.App.ViewModels;
using PidSimulator.Core;
using ScottPlot;
using Color = ScottPlot.Color;

namespace PidSimulator.App.Views;

/// <summary>
/// SP/PV/MV のリアルタイムトレンド（仕様 §8）。
/// 描画はビュー固有の処理なのでコードビハインドで行う。演算スレッドのデータはロックしてコピーしてから描く。
/// </summary>
public partial class TrendView : UserControl
{
    private const int MaxPoints = 2000;

    private static readonly Color CSp = Color.FromHex("#3B4755");
    private static readonly Color CPv = Color.FromHex("#1D5C9E");
    private static readonly Color CMv = Color.FromHex("#C45E0C");
    private static readonly Color CForce = Color.FromHex("#FFF0BF");
    private static readonly Color CGrid = Color.FromHex("#E0E5EA");
    private static readonly Color CAxis = Color.FromHex("#737F8C");
    private static readonly Color CCursor = Color.FromHex("#445162");

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly List<TrendSample> _buffer = [];
    private double _window = 120;
    private bool _paused;
    private double _pausedAt;
    private bool _limitsPending = true;
    private double? _cursorX;

    public TrendView()
    {
        InitializeComponent();
        SetupPlot();
        DataContextChanged += (_, _) =>
        {
            _limitsPending = true;
            _cursorX = null;
            Render();
        };
        _timer.Tick += (_, _) => Render();
        Loaded += (_, _) => { _timer.Start(); Render(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private DetailViewModel? Vm => DataContext as DetailViewModel;

    private void SetupPlot()
    {
        var p = Plot.Plot;
        p.Font.Set("Yu Gothic UI");
        p.FigureBackground.Color = Colors.White;
        p.DataBackground.Color = Colors.White;
        p.Axes.Color(CAxis);
        p.Grid.MajorLineColor = CGrid;
        var timeAxis = p.Axes.DateTimeTicksBottom();
        timeAxis.TickGenerator = new ScottPlot.TickGenerators.DateTimeAutomatic
        {
            LabelFormatter = dt => dt.ToString("HH:mm:ss"),
        };
        p.Axes.Right.Label.Text = "MV [%]";
        p.Axes.Right.Label.ForeColor = CMv;
        p.Axes.Right.TickLabelStyle.ForeColor = CMv;
        p.Axes.Right.Label.FontSize = 12;
        p.Axes.Left.Label.FontSize = 12;
        p.Axes.Left.TickLabelStyle.FontSize = 11;
        p.Axes.Right.TickLabelStyle.FontSize = 11;
        p.Axes.Bottom.TickLabelStyle.FontSize = 11;
        Plot.UserInputProcessor.Disable();
    }

    private void Render()
    {
        var vm = Vm;
        if (vm == null || !IsVisible) return;
        var target = vm.Target.Model;
        var engine = vm.Main.Engine;

        double tEnd = _paused ? _pausedAt : engine.T;
        double tStart = tEnd - _window;
        _buffer.Clear();
        lock (target.Sync) target.Trend.CopyRange(tStart - 0.5, tEnd, _buffer);

        var p = Plot.Plot;
        p.Clear();
        p.Axes.Left.Label.Text = $"SP / PV [{target.PvRange.Unit}]";
        p.Axes.Right.Label.Text = $"MV [{target.MvRange.Unit}]";

        TrendSample? pick = null;
        if (_buffer.Count >= 2)
        {
            int step = Math.Max(1, _buffer.Count / MaxPoints);
            var data = _buffer.Where((_, i) => i % step == 0 || i == _buffer.Count - 1).ToArray();
            double[] xs = data.Select(s => engine.ToTime(s.T).ToOADate()).ToArray();

            AddForceSpans(p, data, xs);
            IYAxis right = p.Axes.Right;
            if (ChkMv.IsChecked == true) AddLine(p, xs, data.Select(s => s.Mv).ToArray(), CMv, 1.4f, right);
            if (ChkSp.IsChecked == true)
            {
                var sp = AddLine(p, xs, data.Select(s => s.Sp).ToArray(), CSp, 1.4f, null);
                sp.LinePattern = LinePattern.Dashed;
                sp.ConnectStyle = ConnectStyle.StepHorizontal;
            }
            if (ChkPv.IsChecked == true) AddLine(p, xs, data.Select(s => s.Pv).ToArray(), CPv, 2f, null);

            var last = data[^1];
            if (ChkPv.IsChecked == true) AddEndMarker(p, xs[^1], last.Pv, CPv, null);
            if (ChkMv.IsChecked == true) AddEndMarker(p, xs[^1], last.Mv, CMv, right);

            pick = last;
            if (_cursorX is double cx && cx >= xs[0] && cx <= xs[^1])
            {
                int i = Array.BinarySearch(xs, cx);
                if (i < 0) i = Math.Clamp(~i, 0, xs.Length - 1);
                pick = data[i];
                var line = p.Add.VerticalLine(xs[i]);
                line.Color = CCursor;
                line.LineWidth = 1;
                line.LinePattern = LinePattern.Dotted;
            }
        }

        if (!_paused || _limitsPending)
        {
            p.Axes.SetLimitsX(engine.ToTime(tStart).ToOADate(), engine.ToTime(tEnd).ToOADate());
            var (lo, hi) = PvLimits(target);
            p.Axes.SetLimitsY(lo, hi);
            p.Axes.SetLimitsY(target.MvRange.EngMin, target.MvRange.EngMax, p.Axes.Right);
            _limitsPending = false;
        }
        Plot.Refresh();
        UpdateReadout(pick, target, engine);
    }

    private (double lo, double hi) PvLimits(ControlTarget t)
    {
        double lo = t.PvRange.EngMin, hi = t.PvRange.EngMax;
        if (CmbY.SelectedIndex != 1 || _buffer.Count == 0) return (lo, hi);

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var s in _buffer)
        {
            if (ChkPv.IsChecked == true) { min = Math.Min(min, s.Pv); max = Math.Max(max, s.Pv); }
            if (ChkSp.IsChecked == true) { min = Math.Min(min, s.Sp); max = Math.Max(max, s.Sp); }
        }
        if (!double.IsFinite(min)) return (lo, hi);
        double span = Math.Max(max - min, (t.PvRange.EngMax - t.PvRange.EngMin) * 0.05);
        return (min - span * 0.15, max + span * 0.15);
    }

    private static ScottPlot.Plottables.Scatter AddLine(Plot p, double[] xs, double[] ys, Color color, float width, IYAxis? axis)
    {
        var s = p.Add.Scatter(xs, ys);
        s.Color = color;
        s.LineWidth = width;
        s.MarkerSize = 0;
        if (axis != null) s.Axes.YAxis = axis;
        return s;
    }

    private static void AddEndMarker(Plot p, double x, double y, Color color, IYAxis? axis)
    {
        var m = p.Add.Marker(x, y);
        m.Color = color;
        m.Size = 7;
        if (axis != null) m.Axes.YAxis = axis;
    }

    private static void AddForceSpans(Plot p, TrendSample[] data, double[] xs)
    {
        int start = -1;
        for (int i = 0; i < data.Length; i++)
        {
            bool on = data[i].Forced;
            if (on && start < 0) start = i;
            if (start >= 0 && (!on || i == data.Length - 1))
            {
                var span = p.Add.HorizontalSpan(xs[start], xs[i]);
                span.FillStyle.Color = CForce;
                span.LineStyle.Width = 0;
                start = -1;
            }
        }
    }

    private void UpdateReadout(TrendSample? s, ControlTarget t, SimulationEngine engine)
    {
        if (s is not { } v)
        {
            RoLabel.Text = "データなし";
            RoTime.Text = RoSp.Text = RoPv.Text = RoMv.Text = RoDist.Text = "";
            RoForce.Visibility = Visibility.Collapsed;
            return;
        }
        string f = "F" + t.Info.Decimals;
        RoLabel.Text = _cursorX != null ? "カーソル" : "最新";
        RoTime.Text = engine.ToTime(v.T).ToString("HH:mm:ss.f");
        RoSp.Text = $"{v.Sp.ToString(f)} {t.SpRange.Unit}";
        RoPv.Text = $"{v.Pv.ToString(f)} {t.PvRange.Unit}";
        RoMv.Text = $"{v.Mv:F1} {t.MvRange.Unit}";
        RoDist.Text = $"{v.Dist:F1} {t.Info.DistUnit}";
        RoForce.Visibility = v.Forced ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (CmbWindow.SelectedItem is ComboBoxItem { Tag: string tag }) _window = double.Parse(tag);
        _limitsPending = true;
        Render();
    }

    private void Pause_Changed(object sender, RoutedEventArgs e)
    {
        _paused = BtnPause.IsChecked == true;
        BtnPause.Content = _paused ? "▶ 再開" : "❚❚ 一時停止";
        PauseHint.Visibility = _paused ? Visibility.Visible : Visibility.Collapsed;
        if (Vm is { } vm) _pausedAt = vm.Main.Engine.T;
        if (_paused) Plot.UserInputProcessor.Enable();
        else Plot.UserInputProcessor.Disable();
        _limitsPending = true;
        Render();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (!Dialogs.Confirm("履歴クリア", "トレンド履歴を消去します。")) return;
        vm.Target.Model.ClearTrend();
        Render();
    }

    private void Plot_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(Plot);
        var px = new Pixel((float)(pos.X * Plot.DisplayScale), (float)(pos.Y * Plot.DisplayScale));
        _cursorX = Plot.Plot.GetCoordinates(px).X;
        if (_paused) Render();
    }

    private void Plot_MouseLeave(object sender, MouseEventArgs e)
    {
        _cursorX = null;
        if (_paused) Render();
    }
}
