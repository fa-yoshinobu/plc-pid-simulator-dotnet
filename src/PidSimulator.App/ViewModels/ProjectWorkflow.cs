using System.IO;
using PidSimulator.Core;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;

namespace PidSimulator.App.ViewModels;

internal sealed class ProjectWorkflow(MainViewModel main, IUserInteraction interaction)
{
    private ProjectDocument _loadedDocument = new();

    public void New()
    {
        if (!ConfirmLeave("新規プロジェクト")) return;
        if (!LoadDocument(new ProjectDocument(), null)) return;
        main.Notify("新規プロジェクトを作成しました");
    }

    public void Open()
    {
        if (!ConfirmLeave("プロジェクトを開く")) return;
        if (interaction.OpenProjectFile() is { } path) OpenFile(path);
    }

    public void OpenDemo()
    {
        if (!ConfirmLeave("DEMOを開く")) return;
        OpenFile(Path.Combine(AppContext.BaseDirectory, ProjectSerializer.DemoFileName));
    }

    private void OpenFile(string path)
    {
        try
        {
            var document = ProjectSerializer.Load(path);
            if (!LoadDocument(document, path)) return;
            main.Notify($"{Path.GetFileName(path)} を開きました（{document.Targets.Count} 件）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            App.Diagnostics.Write("プロジェクト読込み失敗", ex);
            interaction.Error("プロジェクトを開けません", $"{path}\n\n{ex.Message}");
        }
    }

    public bool Save() => main.ProjectPath == null || ProjectSerializer.IsDemoFile(main.ProjectPath)
        ? SaveAs() : Write(main.ProjectPath);

    public bool SaveAs()
    {
        string name = ProjectSerializer.IsDemoFile(main.ProjectPath) ? "DEMO_コピー"
            : Path.GetFileNameWithoutExtension(main.ProjectPath) ?? main.ProjectName;
        if (interaction.SaveProjectFile(name) is not { } path) return false;
        if (ProjectSerializer.IsDemoFile(path))
        {
            interaction.Info("別の名前で保存", "DEMO.psim は上書きできません。ファイル名を変更してください。");
            return false;
        }
        return Write(path);
    }

    private bool Write(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        try { ProjectSerializer.Save(path, Snapshot(name)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Diagnostics.Write("プロジェクト保存失敗", ex);
            interaction.Error("保存できません", $"{path}\n\n{ex.Message}");
            return false;
        }
        main.ProjectPath = path;
        main.ProjectName = name;
        main.IsDirty = false;
        main.Engine.Log.Add(null, "登録", $"プロジェクトを保存: {Path.GetFileName(path)}");
        main.Notify($"{Path.GetFileName(path)} に保存しました");
        return true;
    }

    private ProjectDocument Snapshot(string name) => _loadedDocument.Snapshot(
        name, main.PlcSettings, main.DataSettings, main.Engine.Targets.Select(t => t.ToConfig()));

    public string? SaveRecovery()
    {
        if (!main.IsDirty) return null;
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PidSimulator", "recovery");
        return ProjectRecovery.Save(Snapshot(main.ProjectName), main.ProjectPath, directory,
            error => App.Diagnostics.Write("元の場所への回復保存失敗", error));
    }

    private bool LoadDocument(ProjectDocument document, string? path)
    {
        // Reject invalid connection settings before discarding the current project or its unknown fields.
        if (!main.ApplyPlcSettings(document.Plc, force: true)) return false;
        main.CancelStartAll();
        (main.CurrentPage as DetailViewModel)?.Detach();
        main.CurrentPage = main.Overview;
        main.Engine.Clear();
        main.Dummy.ClearLoops(resetState: true);
        main.Targets.Clear();
        _loadedDocument = document;
        main.DataSettings = document.Data;
        main.Engine.SetTrendRetention(document.Data.TrendMinutes);
        foreach (var config in document.Targets)
        {
            var target = ControlTarget.FromConfig(config);
            main.Engine.Add(target);
            main.Dummy.AddLoop(DummyPidLoop.For(target));
            main.Targets.Add(new TargetViewModel(main, target));
        }
        main.ProjectPath = path;
        main.ProjectName = path != null ? Path.GetFileNameWithoutExtension(path) : document.Name;
        main.IsDirty = false;
        main.Engine.Log.Add(null, "登録", path != null ? $"プロジェクトを開く: {Path.GetFileName(path)}" : "新規プロジェクト");
        main.UpdateSummary();
        return true;
    }

    public bool ConfirmLeave(string action)
    {
        if (main.Targets.Any(t => !t.IsStopped))
        {
            if (!interaction.Confirm(action, "制御中またはプレビュー中の対象があります。すべて停止してから続けます。", "停止して続ける", danger: true))
                return false;
            main.Engine.StopAll(action);
        }
        if (!main.IsDirty) return true;
        return interaction.AskSave(main.ProjectName) switch { 0 => Save(), 1 => true, _ => false };
    }
}
