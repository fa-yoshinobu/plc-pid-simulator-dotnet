using System.Windows;
using PidSimulator.App.ViewModels;
using PidSimulator.Core;
using PidSimulator.Core.Plc;

namespace PidSimulator.App;

public partial class App : Application
{
    private SimulationEngine? _engine;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var plc = new DummyPlc();
        _engine = new SimulationEngine(plc);
        _engine.Run();

        var window = new MainWindow { DataContext = new MainViewModel(_engine, plc, "新規プロジェクト") };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var plc = _engine?.Plc;
        _engine?.Dispose();
        if (plc is not DummyPlc) plc?.Dispose();
        base.OnExit(e);
    }
}
