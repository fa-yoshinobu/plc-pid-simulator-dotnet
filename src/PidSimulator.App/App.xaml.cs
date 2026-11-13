using System.Windows;
using System.Windows.Threading;
using PidSimulator.App.ViewModels;
using PidSimulator.Core;
using PidSimulator.Core.Plc;

namespace PidSimulator.App;

public partial class App : Application
{
    private SimulationEngine? _engine;
    private MainViewModel? _viewModel;
    private int _handlingError;
    public bool IsTerminating { get; private set; }
    public static DiagnosticLog Diagnostics { get; } = new(DiagnosticLog.DefaultDirectory);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherError;
        AppDomain.CurrentDomain.UnhandledException += OnDomainError;
        TaskScheduler.UnobservedTaskException += OnTaskError;
        var plc = new DummyPlc();
        _engine = new SimulationEngine(plc);
        _engine.Error += (error, context) => Diagnostics.Write(context, error);
        _engine.Log.Added += entry =>
        {
            if (entry.Category is "通信" or "異常")
                Diagnostics.Write($"{entry.TargetName} {entry.Category} {entry.Message}");
        };
        _engine.Run();
        _viewModel = new MainViewModel(_engine, plc, "新規プロジェクト");
        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ReportUnhandled(e.Exception, e.Exception is OutOfMemoryException or AccessViolationException);
    }

    private void OnDomainError(object sender, UnhandledExceptionEventArgs e)
    {
        var error = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString());
        Diagnostics.Write("未処理の背景スレッド例外", error);
        try
        {
            Dispatcher.Invoke(() => ReportUnhandled(error, e.IsTerminating), DispatcherPriority.Send, TimeSpan.FromSeconds(3));
        }
        catch (Exception dispatchError)
        {
            Diagnostics.Write("回復処理を画面スレッドで実行できません", dispatchError);
        }
    }

    private void OnTaskError(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        Diagnostics.Write("未処理のタスク例外", e.Exception);
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(() => ReportUnhandled(e.Exception, terminating: false));
    }

    private void ReportUnhandled(Exception error, bool terminating)
    {
        Diagnostics.Write("予期しないエラー", error);
        if (Interlocked.Exchange(ref _handlingError, 1) != 0) return;
        try
        {
            try { _engine?.StopAll("予期しないエラー"); }
            catch (Exception stopError)
            {
                Diagnostics.Write("全制御停止でエラー", stopError);
                _engine?.Dispose();
                terminating = true;
            }
            string? recovered = null;
            try { recovered = _viewModel?.SaveRecovery(); }
            catch (Exception recoveryError) { Diagnostics.Write("回復保存に失敗", recoveryError); }
            string logDirectory = Diagnostics.DirectoryPath;
            try { logDirectory = Diagnostics.GetExistingDirectoryPath(); }
            catch (Exception pathError) { Diagnostics.Write("診断ログの実保存先を取得できません", pathError); }
            string message = $"予期しないエラーが発生しました。\n{error.Message}\n\n"
                + (recovered != null ? $"未保存の内容を回復保存しました。\n{recovered}\n\n" : "")
                + $"診断ログ：{logDirectory}";
            bool continueRunning = !terminating && MessageBox.Show(message + "\n\n全制御を停止しました。画面の操作を続けますか？",
                "エラーと回復保存", MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (continueRunning) return;
            IsTerminating = true;
            if (terminating) MessageBox.Show(message, "エラーと回復保存", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
        finally { Volatile.Write(ref _handlingError, 0); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherError;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainError;
        TaskScheduler.UnobservedTaskException -= OnTaskError;
        var plc = _engine?.Plc;
        _engine?.Dispose();
        if (plc is not DummyPlc) plc?.Dispose();
        base.OnExit(e);
    }
}
