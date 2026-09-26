using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PidSimulator.Core;
using PidSimulator.Core.Models;
using PidSimulator.Core.Project;

namespace PidSimulator.App.ViewModels;

public sealed partial class ConditionInput : ObservableObject
{
    public ConditionInput(ConditionField field) { Field = field; _text = field.Default.ToString("G"); }
    public ConditionField Field { get; }
    public string AllowedRange => $"入力範囲: {Field.Min:G} ～ {Field.Max:G} {Field.Unit}";
    [ObservableProperty] private string _text;
}
public sealed record ParameterPreview(string Label, string Unit, string Before, string After);

public sealed partial class ParameterWizardViewModel : ObservableObject
{
    private readonly ModelKind _kind;
    private readonly IReadOnlyDictionary<string, double> _current;
    private readonly TargetConfig? _target;
    public ParameterWizardViewModel(ModelKind kind, IReadOnlyDictionary<string, double> current, TargetConfig? target = null)
    {
        _kind = kind; _current = current; _target = target;
        ModelName = ModelCatalog.Get(kind).Name;
        Scenarios = ParameterCalculator.Scenarios(kind);
        _selectedScenario = Scenarios[0];
        LoadFields();
    }
    public string ModelName { get; }
    public IReadOnlyList<ControlScenario> Scenarios { get; }
    public ObservableCollection<ConditionInput> Inputs { get; } = [];
    public ObservableCollection<ConditionInput> BasicInputs { get; } = [];
    public ObservableCollection<ConditionInput> AdvancedInputs { get; } = [];
    public ObservableCollection<ParameterPreview> Preview { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public bool HasAdvanced => AdvancedInputs.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;
    public CalculatedParameters? Result { get; private set; }
    public event Action? ApplyRequested;
    [ObservableProperty] private ControlScenario _selectedScenario;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StepLabel), nameof(NextLabel), nameof(CanBack))] private int _stepIndex;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _explanation = "";
    public string StepLabel => $"{StepIndex + 1} / 3　{new[] { "よくある制御を選ぶ", "設備の条件を入力", "計算結果を確認" }[StepIndex]}";
    public string NextLabel => StepIndex == 0 ? "条件を入力 →" : StepIndex == 1 ? "計算する →" : "パラメータに適用";
    public bool CanBack => StepIndex > 0;
    partial void OnSelectedScenarioChanged(ControlScenario value) => LoadFields();
    private void LoadFields()
    {
        Inputs.Clear();
        BasicInputs.Clear();
        AdvancedInputs.Clear();
        foreach (var field in SelectedScenario.Fields)
        {
            var row = new ConditionInput(field);
            Inputs.Add(row);
            (field.Advanced ? AdvancedInputs : BasicInputs).Add(row);
        }
        OnPropertyChanged(nameof(HasAdvanced));
        Result = null;
    }
    [RelayCommand]
    private void Next()
    {
        Error = "";
        if (StepIndex == 0) { StepIndex = 1; return; }
        if (StepIndex == 2) { ApplyRequested?.Invoke(); return; }
        try
        {
            var inputs = new Dictionary<string, double>();
            foreach (var row in Inputs)
            {
                if (!double.TryParse(row.Text, out var value)) throw new ArgumentException($"{row.Field.Label}を数値で入力してください。");
                inputs[row.Field.Key] = value;
            }
            Result = ParameterCalculator.Calculate(_kind, SelectedScenario, inputs);
            Preview.Clear();
            foreach (var def in ModelCatalog.Get(_kind).Params)
                if (Result.Values.TryGetValue(def.Key, out var value))
                    Preview.Add(new(def.Label, def.Unit, PreviewValue(def, _current.GetValueOrDefault(def.Key, def.Default)), PreviewValue(def, value)));
            UpdateWarnings(inputs, Result.Values);
            Explanation = Result.Explanation;
            StepIndex = 2;
        }
        catch (ArgumentException ex) { Error = ex.Message; }
    }
    [RelayCommand]
    private void Back()
    {
        if (StepIndex > 0) StepIndex--;
        Error = "";
        Result = null;
    }

    private static string PreviewValue(ParamDef def, double value) =>
        def.Choices.Count > 0 && value == Math.Truncate(value) && value >= 0 && value < def.Choices.Count
            ? def.Choices[(int)value] : value.ToString("G5");

    private void UpdateWarnings(IReadOnlyDictionary<string, double> inputs, IReadOnlyDictionary<string, double> values)
    {
        Warnings.Clear();
        if (_target is { } target)
        {
            bool Outside(double value, RangeDef range) => value < range.EngMin || value > range.EngMax;
            bool Different(double a, double b) => Math.Abs(a - b) > 1e-6 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
            if (values.TryGetValue("height", out var tankHeight) && target.PvRange.Unit == "mm")
            {
                Warnings.Add($"計算後の満水液位は {tankHeight:G} mmです。PV・SPレンジと初期PV・内部SPは変更しません。登録内容編集でタンク高さに合う値になっているか確認してください。");
                if (Different(_current.GetValueOrDefault("height", tankHeight), tankHeight))
                    Warnings.Add("タンク高さの変更により、現在状態を設定済みの初期PVへ戻し、経過時間・トレンド履歴をリセットします。外乱の予約も取り消します。");
                if (target.InitialPv > tankHeight || (!target.UseSp && target.InternalSp > tankHeight))
                    Warnings.Add("初期PVまたは内部SPが、計算後のタンク高さを超えています。");
            }
            if (inputs.TryGetValue("initial", out var initial) && inputs.TryGetValue("target", out var goal))
            {
                if (Different(initial, target.InitialPv))
                    Warnings.Add($"計算の開始温度は {initial:G} ℃、登録済みの初期PVは {target.InitialPv:G} ℃です。同じ条件で試す場合は、登録内容編集で初期PVを変更してからリセットしてください。");
                if (target.UseSp)
                    Warnings.Add($"計算の目標温度は {goal:G} ℃です。SPはPLCから読み込む設定なので、PLC側のSPを確認してください。");
                else if (Different(goal, target.InternalSp))
                    Warnings.Add($"計算の目標温度は {goal:G} ℃、登録済みの内部SPは {target.InternalSp:G} ℃です。同じ条件で試す場合は、登録内容編集で内部SPを変更してください。");
                if (Outside(initial, target.PvRange) || Outside(goal, target.PvRange))
                    Warnings.Add($"計算の温度がPVレンジ {target.PvRange.EngMin:G} ～ {target.PvRange.EngMax:G} {target.PvRange.Unit} の外です。登録内容編集でレンジを確認してください。");
                if (target.UseSp && Outside(goal, target.SpRange))
                    Warnings.Add($"計算の目標温度がSPレンジ {target.SpRange.EngMin:G} ～ {target.SpRange.EngMax:G} {target.SpRange.Unit} の外です。PLC側と本アプリのSPレンジを合わせてください。");
            }
            string? maxKey = _kind switch
            {
                ModelKind.Motor => "nmax",
                ModelKind.Flow or ModelKind.PumpFlow => "qmax",
                ModelKind.PumpPressure => "pmax",
                ModelKind.PressureSupplyValve => "psupply",
                _ => null,
            };
            if (maxKey != null && values.TryGetValue(maxKey, out var maximum))
            {
                if (_kind == ModelKind.PressureSupplyValve)
                    Warnings.Add($"供給圧力 {maximum:G5} {target.PvRange.Unit} は無消費時の上限です。空気の消費・漏れがあると到達できる圧力はさらに低くなります。");
                if (target.InitialPv > maximum)
                    Warnings.Add($"登録済みの初期PV {target.InitialPv:G} {target.PvRange.Unit} が、計算後の最大値 {maximum:G5} {target.PvRange.Unit} を超えています。登録内容編集で初期PVを確認してください。");
                if (!target.UseSp && (target.InternalSp < 0 || target.InternalSp > maximum))
                    Warnings.Add($"登録済みの内部SP {target.InternalSp:G} {target.SpRange.Unit} は計算後の到達範囲 0 ～ {maximum:G5} {target.PvRange.Unit} の外です。登録内容編集で内部SPを変更してください。");
                if (target.UseSp && (target.SpRange.EngMin < 0 || target.SpRange.EngMax > maximum))
                    Warnings.Add($"SPレンジは {target.SpRange.EngMin:G} ～ {target.SpRange.EngMax:G} {target.SpRange.Unit} のままです。計算後の到達範囲 0 ～ {maximum:G5} {target.PvRange.Unit} の外にあるPLCのSPには到達できません。");
                if (maximum > target.PvRange.EngMax)
                    Warnings.Add($"計算後の最大値 {maximum:G5} {target.PvRange.Unit} がPVレンジ上限 {target.PvRange.EngMax:G} {target.PvRange.Unit} を超えています。上限を超えたPVのPLC書込値は上限に制限されます。登録内容編集でレンジを確認してください。");
            }
        }
        OnPropertyChanged(nameof(HasWarnings));
    }
}
