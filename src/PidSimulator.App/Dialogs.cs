using PidSimulator.App.Views;
using PidSimulator.Core.Project;

namespace PidSimulator.App;

/// <summary>確認・エラー表示。アプリの配色に合わせた <see cref="DialogWindow"/> を使う。</summary>
public static class Dialogs
{
    public static bool Confirm(string title, string message, string okLabel = "OK", bool danger = false,
        IEnumerable<CheckResult>? items = null) =>
        DialogWindow.Show(title, message, DialogKind.Warning,
        [
            new DialogButton("キャンセル", IsCancel: true),
            new DialogButton(okLabel, danger ? "BtnDanger" : "BtnPrimary", IsDefault: !danger),
        ], items) == 1;

    public static void Error(string title, string message, IEnumerable<CheckResult>? items = null) =>
        DialogWindow.Show(title, message, DialogKind.Error, [new DialogButton("閉じる", "BtnPrimary", IsDefault: true, IsCancel: true)], items);

    public static void Info(string title, string message) =>
        DialogWindow.Show(title, message, DialogKind.Info, [new DialogButton("閉じる", "BtnPrimary", IsDefault: true, IsCancel: true)]);

    /// <summary>未保存の変更の扱い。0=保存する 1=保存しない 2=キャンセル</summary>
    public static int AskSave(string projectName) =>
        DialogWindow.Show("未保存の変更", $"「{projectName}」の変更が保存されていません。保存しますか？", DialogKind.Warning,
        [
            new DialogButton("保存する", "BtnPrimary", IsDefault: true),
            new DialogButton("保存しない"),
            new DialogButton("キャンセル", IsCancel: true),
        ]);
}
