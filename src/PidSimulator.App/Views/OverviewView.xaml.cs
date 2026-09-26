using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PidSimulator.App.Views;

public partial class OverviewView : UserControl
{
    public OverviewView() => InitializeComponent();

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.DataContext = button.DataContext;
        menu.IsOpen = true;
    }
}
