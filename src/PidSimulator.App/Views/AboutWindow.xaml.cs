using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace PidSimulator.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var app = typeof(AboutWindow).Assembly;
        ProductName = app.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "PID Process Simulator";
        Version = VersionOf(app);
        Company = app.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "FA Labo";
        Copyright = app.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "Copyright (c) 2026 FA Labo";
        Libraries =
        [
            new(ProductName, Version, "MIT", "アプリ本体・内部モジュール"),
            new("CommunityToolkit.Mvvm", VersionOf("CommunityToolkit.Mvvm"), "MIT", "画面とデータの連携"),
            new("PlcComm.Slmp", VersionOf("PlcComm.Slmp"), "MIT", "MELSEC SLMP通信"),
            new("PlcComm.KvHostLink", VersionOf("PlcComm.KvHostLink"), "MIT", "KEYENCE Host Link通信"),
            new("ScottPlot", VersionOf("ScottPlot"), "MIT", "トレンドグラフ"),
            new("ScottPlot.WPF", VersionOf("ScottPlot.WPF"), "MIT", "WPFへのグラフ表示"),
            new("SkiaSharp", VersionOf("SkiaSharp"), "MIT", "グラフの描画基盤"),
            new("SkiaSharp.Views.WPF", VersionOf("SkiaSharp.Views.WPF"), "MIT", "描画基盤のWPF連携"),
            new(".NET Runtime", Environment.Version.ToString(), "MIT", "アプリの実行環境"),
        ];
        using var stream = app.GetManifestResourceStream("PidSimulator.App.LICENSE");
        using var reader = stream == null ? null : new StreamReader(stream);
        LicenseText = reader?.ReadToEnd() ?? "ライセンス本文を読み込めませんでした。";
        DataContext = this;
    }

    public string ProductName { get; }
    public string Version { get; }
    public string Company { get; }
    public string Copyright { get; }
    public string LicenseText { get; }
    public IReadOnlyList<LibraryInfo> Libraries { get; }

    private static string VersionOf(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info) ? assembly.GetName().Version?.ToString() ?? "不明" : info.Split('+')[0];
    }

    private static string VersionOf(string name)
    {
        try { return VersionOf(Assembly.Load(new AssemblyName(name))); }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return "不明";
        }
    }

    private void OpenRepository(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            Dialogs.Error("リンクを開けません", "ブラウザーを起動できませんでした。");
        }
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    public sealed record LibraryInfo(string Name, string Version, string License, string Purpose);
}
