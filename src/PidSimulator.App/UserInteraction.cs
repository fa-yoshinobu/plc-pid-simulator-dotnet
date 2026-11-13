using System.Windows;
using Microsoft.Win32;
using PidSimulator.App.ViewModels;
using PidSimulator.App.Views;
using PidSimulator.Core.Project;

namespace PidSimulator.App;

public interface IUserInteraction
{
    bool Confirm(string title, string message, string okLabel = "OK", bool danger = false, IEnumerable<CheckResult>? items = null);
    void Error(string title, string message, IEnumerable<CheckResult>? items = null);
    void Info(string title, string message);
    int AskSave(string projectName);
    string? OpenProjectFile();
    string? SaveProjectFile(string defaultName);
    TargetConfig? EditTarget(RegistrationViewModel viewModel);
    bool EditSettings(SettingsViewModel viewModel);
}

internal sealed class WpfUserInteraction : IUserInteraction
{
    private const string FileFilter = "PIDシミュレータ プロジェクト (*.psim)|*.psim";

    public bool Confirm(string title, string message, string okLabel = "OK", bool danger = false, IEnumerable<CheckResult>? items = null) =>
        Dialogs.Confirm(title, message, okLabel, danger, items);
    public void Error(string title, string message, IEnumerable<CheckResult>? items = null) => Dialogs.Error(title, message, items);
    public void Info(string title, string message) => Dialogs.Info(title, message);
    public int AskSave(string projectName) => Dialogs.AskSave(projectName);

    public string? OpenProjectFile()
    {
        var picker = new OpenFileDialog { Filter = FileFilter, Title = "プロジェクトを開く" };
        return picker.ShowDialog() == true ? picker.FileName : null;
    }

    public string? SaveProjectFile(string defaultName)
    {
        var picker = new SaveFileDialog
        {
            Filter = FileFilter, Title = "名前を付けて保存", FileName = defaultName,
            DefaultExt = ProjectSerializer.Extension,
        };
        picker.FileOk += (_, e) =>
        {
            if (!ProjectSerializer.IsDemoFile(picker.FileName)) return;
            e.Cancel = true;
            Info("別の名前で保存", "DEMO.psim は上書きできません。ファイル名を変更してください。");
        };
        return picker.ShowDialog() == true ? picker.FileName : null;
    }

    public TargetConfig? EditTarget(RegistrationViewModel viewModel)
    {
        var window = new RegistrationWindow(viewModel) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? viewModel.Result : null;
    }

    public bool EditSettings(SettingsViewModel viewModel)
    {
        var window = new SettingsWindow(viewModel) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true && viewModel.Saved;
    }
}
