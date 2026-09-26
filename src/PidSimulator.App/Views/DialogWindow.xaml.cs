using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PidSimulator.Core.Project;

namespace PidSimulator.App.Views;

public enum DialogKind { Info, Warning, Error }

public sealed record DialogButton(string Label, string StyleKey = "Btn", bool IsDefault = false, bool IsCancel = false);

/// <summary>アプリの配色に合わせた確認・通知ダイアログ。押されたボタンの番号を返す（閉じた場合はキャンセル扱い）。</summary>
public partial class DialogWindow : Window
{
    private int _result = -1;

    private DialogWindow() => InitializeComponent();

    public static int Show(string title, string message, DialogKind kind, IReadOnlyList<DialogButton> buttons,
        IEnumerable<CheckResult>? items = null)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        var dlg = new DialogWindow { Title = title };
        if (owner is { IsLoaded: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        var list = items?.ToList();
        dlg.ItemList.ItemsSource = list;
        dlg.ItemList.Visibility = list is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        (dlg.GlyphCircle.Fill, dlg.GlyphText.Text) = kind switch
        {
            DialogKind.Error => ((System.Windows.Media.Brush)dlg.FindResource("AlarmBrush"), "×"),
            DialogKind.Warning => ((System.Windows.Media.Brush)dlg.FindResource("ForceLineBrush"), "!"),
            _ => ((System.Windows.Media.Brush)dlg.FindResource("AccentBrush"), "i"),
        };

        int cancelIndex = -1;
        for (int i = 0; i < buttons.Count; i++)
        {
            var spec = buttons[i];
            int index = i;
            var b = new Button
            {
                Content = spec.Label,
                Style = (Style)dlg.FindResource(spec.StyleKey),
                IsDefault = spec.IsDefault,
                IsCancel = spec.IsCancel,
                MinWidth = 88,
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
            };
            b.Click += (_, _) => { dlg._result = index; dlg.Close(); };
            dlg.ButtonPanel.Children.Add(b);
            if (spec.IsCancel) cancelIndex = i;
            if (spec.IsDefault) dlg.Loaded += (_, _) => b.Focus();
        }
        dlg.ShowDialog();
        return dlg._result < 0 ? cancelIndex : dlg._result;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
