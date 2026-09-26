using System.Windows;
using PidSimulator.App.ViewModels;

namespace PidSimulator.App.Views;

public partial class ParameterWizardWindow : Window
{
    public ParameterWizardWindow(ParameterWizardViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.ApplyRequested += () => DialogResult = true;
    }
}
