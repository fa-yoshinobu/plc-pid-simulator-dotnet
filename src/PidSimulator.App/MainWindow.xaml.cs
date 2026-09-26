using System.ComponentModel;
using System.Windows;
using PidSimulator.App.ViewModels;

namespace PidSimulator.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.ConfirmLeave("終了")) e.Cancel = true;
        base.OnClosing(e);
    }
}
