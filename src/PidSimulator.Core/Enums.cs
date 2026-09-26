namespace PidSimulator.Core;

// 既存の数値を変えないため、新しいモデルは末尾へ追加する。
public enum ModelKind
{
    Motor, Flow, Level, Heater, Steam, Chiller, Pressure,
    PumpFlow, LevelPumpIn, LevelPumpOut, LevelDrainValve, CoolingWater,
    PressureSupplyValve, PressureExhaustValve, PumpPressure,
}

public enum RunState { Stopped, Running, Preview }

public enum CommStatus { Disconnected, Connecting, Ok, Timeout, Error }

public enum ForceKey { Mv, Pv, Sp, Disturbance, ModelInput }

/// <summary>停止時PV（仕様 §7）</summary>
public enum StopPvMode { Hold, Initial, Value }

/// <summary>通信異常時動作（仕様 §14）</summary>
public enum CommErrorAction { StopSimulation, HoldMv, SafeMv, StopPvWrite }

public enum RecoverMode { Auto, Manual }

public static class Labels
{
    public static string Of(ForceKey k) => k switch
    {
        ForceKey.Mv => "MV",
        ForceKey.Pv => "PV",
        ForceKey.Sp => "SP",
        ForceKey.Disturbance => "外乱",
        _ => "モデル入力",
    };

    public static string Of(CommStatus c) => c switch
    {
        CommStatus.Ok => "通信正常",
        CommStatus.Timeout => "タイムアウト",
        CommStatus.Error => "通信異常",
        CommStatus.Connecting => "接続中",
        _ => "未接続",
    };

    public static string Of(CommErrorAction a) => a switch
    {
        CommErrorAction.StopSimulation => "シミュレーション停止",
        CommErrorAction.HoldMv => "MV最終値保持",
        CommErrorAction.SafeMv => "MVを安全値へ変更",
        _ => "PV書込み停止",
    };

    public static string Of(StopPvMode m) => m switch
    {
        StopPvMode.Hold => "最終値保持",
        StopPvMode.Initial => "初期値へ戻す",
        _ => "指定値へ戻す",
    };
}
