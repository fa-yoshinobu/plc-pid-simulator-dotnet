using System.Windows;
using PidSimulator.App.ViewModels;

namespace PidSimulator.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += () =>
        {
            if (IsVisible) DialogResult = true;
        };
    }
}
