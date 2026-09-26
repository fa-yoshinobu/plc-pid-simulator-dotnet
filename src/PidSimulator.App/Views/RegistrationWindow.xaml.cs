using System.Windows;
using PidSimulator.App.ViewModels;

namespace PidSimulator.App.Views;

public partial class RegistrationWindow : Window
{
    public RegistrationWindow(RegistrationViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += ok =>
        {
            if (IsVisible) DialogResult = ok;
        };
    }
}
