using System.Text.RegularExpressions;
using PidSimulator.Core;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;

namespace PidSimulator.App.ViewModels;

internal sealed class TargetRegistrationWorkflow(MainViewModel main, IUserInteraction interaction)
{
    // ---- 登録・編集・複製・削除（§5・§22） ----

    public void Register()
    {
        var cfg = TargetConfig.Default(ModelKind.Level);
        (cfg.MvAddress, cfg.PvAddress, cfg.SpAddress) = NextAddresses();
        var vm = new RegistrationViewModel(RegistrationMode.New, cfg, main.Engine.Targets, main.Engine.Plc, plcSettings: main.PlcSettings);
        if (interaction.EditTarget(vm) is not { } result) return;
        var t = AddTarget(result, null);
        main.Notify($"{t.Name} を登録しました");
    }

    public void Edit(TargetViewModel t)
    {
        if (!t.IsStopped)
        {
            interaction.Info("登録内容の編集", $"{t.Name} は制御中またはプレビュー中です。停止してから編集してください。");
            return;
        }
        var vm = new RegistrationViewModel(RegistrationMode.Edit, t.Model.ToConfig(),
            main.Engine.Targets.Where(x => x != t.Model), main.Engine.Plc, t.Name, main.PlcSettings);
        if (interaction.EditTarget(vm) is not { } cfg) return;
        ControlTarget updated;
        if (cfg.Kind != t.Model.Kind)
        {
            updated = ControlTarget.FromConfig(cfg);
            main.Engine.Replace(t.Model, updated);
        }
        else
        {
            t.Model.ApplySettings(cfg);
            main.Engine.Log.Add(t.Model, "登録", "登録内容を編集");
            updated = t.Model;
        }
        ReplaceViewModel(t, new TargetViewModel(main, updated));
        RebuildDummyLoops();
        main.MarkDirty();
        main.Notify("登録内容を保存しました");
    }

    public void Duplicate(TargetViewModel t)
    {
        var cfg = t.Model.ToConfig();
        cfg.Name = $"{t.Name} のコピー";
        (cfg.MvAddress, cfg.PvAddress, cfg.SpAddress) = NextAddresses();
        var vm = new RegistrationViewModel(RegistrationMode.Duplicate, cfg, main.Engine.Targets, main.Engine.Plc, t.Name, main.PlcSettings);
        if (interaction.EditTarget(vm) is not { } result) return;
        var added = AddTarget(result, $"{t.Name} から複製して登録");
        main.Notify($"{added.Name} を登録しました");
    }

    public void Delete(TargetViewModel t)
    {
        if (!t.IsStopped)
        {
            interaction.Info("削除できません", $"{t.Name} は制御中です。停止してから削除してください。");
            return;
        }
        if (!interaction.Confirm("制御対象の削除", $"{t.Name} を削除します。トレンド履歴と設定も削除されます。\nPV書込先 {t.PvAddress} への書込みは行われなくなります。", "削除", danger: true))
            return;
        if (main.CurrentPage is DetailViewModel d && d.Target == t) main.BackToOverview();
        main.Engine.Remove(t.Model);
        main.Targets.Remove(t);
        RebuildDummyLoops();
        main.MarkDirty();
        main.UpdateSummary();
        main.Notify("削除しました");
    }

    private TargetViewModel AddTarget(TargetConfig cfg, string? extraLog)
    {
        var t = ControlTarget.FromConfig(cfg);
        main.Engine.Add(t);
        if (extraLog != null) main.Engine.Log.Add(t, "登録", extraLog);
        main.Dummy.AddLoop(DummyPidLoop.For(t));
        var vm = new TargetViewModel(main, t);
        main.Targets.Add(vm);
        main.MarkDirty();
        main.UpdateSummary();
        return vm;
    }

    private void ReplaceViewModel(TargetViewModel oldVm, TargetViewModel newVm)
    {
        int i = main.Targets.IndexOf(oldVm);
        if (i >= 0) main.Targets[i] = newVm;
        if (main.CurrentPage is DetailViewModel d && d.Target == oldVm)
        {
            main.Open(newVm);
        }
    }

    /// <summary>ダミーPLCのPIDループを登録内容に合わせて作り直す（実機PLCでは不要）</summary>
    private void RebuildDummyLoops()
    {
        var sps = main.Engine.Targets.ToDictionary(t => t, t =>
        {
            var previous = main.Dummy.FindLoop(t.MvAddress);
            return t.UseSp && previous?.SpRange.Unit == t.SpRange.Unit ? previous.Sp : t.InternalSp;
        });
        main.Dummy.ClearLoops();
        foreach (var (t, sp) in sps) main.Dummy.AddLoop(DummyPidLoop.For(t, sp));
    }

    /// <summary>選択中の通信方式に合うワードデバイスで、既存アドレスの次の10番台を提案する。</summary>
    private (string mv, string pv, string sp) NextAddresses()
    {
        string prefix = main.PlcSettings.Mode == PlcMode.HostLink && !main.PlcSettings.Profile.EndsWith("-xym", StringComparison.Ordinal) ? "DM" : "D";
        int max = main.Engine.Targets
            .SelectMany(t => new[] { t.MvAddress, t.PvAddress, t.SpAddress })
            .Select(a => Regex.Match(a ?? "", $@"^{prefix}(\d+)$", RegexOptions.IgnoreCase))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(990)
            .Max();
        int b = (max / 10 + 1) * 10;
        return ($"{prefix}{b}", $"{prefix}{b + 2}", $"{prefix}{b + 4}"); // FLOAT32 / INT32 は各2ワード使う
    }

}
