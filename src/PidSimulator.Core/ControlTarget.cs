using PidSimulator.Core.Models;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;

namespace PidSimulator.Core;

public sealed class ForceItem
{
    public bool On { get; set; }
    public double Value { get; set; }
}

public sealed class ForceSet
{
    private readonly Dictionary<ForceKey, ForceItem> _items =
        Enum.GetValues<ForceKey>().ToDictionary(k => k, _ => new ForceItem());

    public ForceItem this[ForceKey k] => _items[k];
    public bool Any => _items.Values.Any(i => i.On);
    public ForceKey[] Active => _items.Where(p => p.Value.On).Select(p => p.Key).ToArray();
}

public sealed record TargetSnapshot(
    RunState RunState, CommStatus Comm, string? Alarm,
    double Sp, double Pv, double Mv, double SpRaw, double PvRaw, double MvRaw,
    double Elapsed, bool PvWriting, ForceKey[] ActiveForces,
    bool DisturbanceOn, double DisturbanceValue, double DisturbanceSince, double PreviewMv,
    double ActualDisturbance);

/// <summary>
/// 登録された制御対象1件。設定値と実行状態を持ち、<see cref="Cycle"/> で仕様 §7 の周期処理を行う。
/// 実行状態の読み書きはすべて <see cref="Sync"/> でロックする（演算スレッドとUIスレッドから触るため）。
/// </summary>
public sealed class ControlTarget
{
    private double _spRead;
    private bool _resumeRequested;
    private double? _pendingPvWrite;
    private double _lastPoll;
    private readonly DisturbanceSchedule _disturbanceSchedule = new();

    public ControlTarget(string name, ModelKind kind, int seed = 0)
    {
        Name = name;
        Kind = kind;
        Info = ModelCatalog.Get(kind);
        Model = ModelCatalog.Create(kind, seed);
        MvRange = new RangeDef { EngMin = 0, EngMax = 100, Unit = "%" };
        PvRange = new RangeDef { EngMin = Info.PvMin, EngMax = Info.PvMax, Unit = Info.Unit };
        SpRange = PvRange.Clone();
        InitialPv = Info.InitPv;
        InternalSp = Info.DefaultSp;
        StopPvValue = Info.InitPv;
        _spRead = InternalSp;
        DisturbanceValue = Info.DistDefault;
        Forces[ForceKey.Mv].Value = 50;
        Forces[ForceKey.Pv].Value = Info.InitPv;
        Forces[ForceKey.Sp].Value = Info.DefaultSp;
        Forces[ForceKey.Disturbance].Value = Info.DistDefault;
        Forces[ForceKey.ModelInput].Value = Info.InputDefault;
        Model.Reset(InitialPv);
        Pv = InitialPv;
        Sp = InternalSp;
    }

    // ---- 登録設定（§5） ----
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; set; }
    public string Description { get; set; } = "";
    public ModelKind Kind { get; }
    public ModelInfo Info { get; }
    public ProcessModel Model { get; }
    public string MvAddress { get; set; } = "";
    public bool MvOnOff { get; set; }
    public string MvDataType => MvOnOff ? "BIT" : DataType;
    public string PvAddress { get; set; } = "";
    public string SpAddress { get; set; } = "";
    public bool UseSp { get; set; } = true;
    public string DataType { get; set; } = "INT16";
    public RangeDef MvRange { get; set; }
    public RangeDef PvRange { get; set; }
    public RangeDef SpRange { get; set; }
    public double InitialPv { get; set; }
    public double InternalSp { get; set; }
    public StopPvMode StopPv { get; set; } = StopPvMode.Hold;
    public double StopPvValue { get; set; }
    public CommErrorAction OnCommError { get; set; } = CommErrorAction.StopSimulation;
    public double SafeMv { get; set; }
    public RecoverMode Recover { get; set; } = RecoverMode.Manual;

    // ---- 実行状態 ----
    public object Sync { get; } = new();
    public ForceSet Forces { get; } = new();
    public TrendBuffer Trend { get; } = new(TrendBuffer.CapacityForMinutes(TrendBuffer.DefaultRetentionMinutes));
    public RunState RunState { get; private set; }
    public CommStatus Comm { get; private set; } = CommStatus.Ok;
    public string? Alarm { get; private set; }
    public double Sp { get; private set; }
    public double Pv { get; private set; }
    public double Mv { get; private set; }
    public double Elapsed { get; private set; }
    public bool PvWriting { get; private set; }
    public bool DisturbanceOn { get; private set; }
    public double DisturbanceValue { get; private set; }
    public double DisturbanceSince { get; private set; }
    public double PreviewMv { get; private set; } = 30;

    public TargetSnapshot Snapshot()
    {
        lock (Sync)
        {
            return new TargetSnapshot(RunState, Comm, Alarm, Sp, Pv, Mv,
                SignalRaw(SpRange, Sp, DataType), SignalRaw(PvRange, Pv, DataType), SignalRaw(MvRange, Mv, MvDataType),
                Elapsed, PvWriting, Forces.Active, DisturbanceOn, DisturbanceValue, DisturbanceSince, PreviewMv,
                CurrentDisturbance());
        }
    }

    private static double SignalRaw(RangeDef range, double value, string type)
    {
        double raw = range.ToRaw(value);
        return PlcDataTypes.CanWrite(type, raw) ? PlcDataTypes.Clamp(type, raw) : raw;
    }

    private double PvToModel(double value) => EngineeringUnits.PvToModel(Kind, PvRange.Unit, Model.P, value);
    private double PvFromModel(double value) => EngineeringUnits.PvFromModel(Kind, PvRange.Unit, Model.P, value);

    public (bool On, double Value) GetForce(ForceKey key)
    {
        lock (Sync) return (Forces[key].On, Forces[key].Value);
    }

    private double CurrentDisturbance() =>
        Forces[ForceKey.Disturbance].On ? Forces[ForceKey.Disturbance].Value
        : _disturbanceSchedule.State == DisturbanceScheduleState.Active ? _disturbanceSchedule.Value
        : DisturbanceOn ? DisturbanceValue : 0;

    // ---- 操作（UIスレッドから呼ばれる） ----

    public bool Start(EventLog log, out string? error)
    {
        lock (Sync)
        {
            error = null;
            if (RunState == RunState.Running) return true;
            if (Comm != CommStatus.Ok)
            {
                error = $"通信が正常ではありません（{Labels.Of(Comm)}）。通信を復旧してから開始してください。";
                return false;
            }
            if (!MvRange.IsValid || !PvRange.IsValid || !SpRange.IsValid)
            {
                error = "レンジの最小値と最大値が正しくありません。";
                return false;
            }
            RunState = RunState.Running;
            log.Add(this, "運転", "制御開始");
            return true;
        }
    }

    public bool Stop(EventLog log, string? reason = null)
    {
        lock (Sync) return StopCore(log, reason);
    }

    private bool StopCore(EventLog log, string? reason)
    {
        CancelDisturbanceScheduleCore(log);
        if (RunState == RunState.Stopped) return false;
        bool wasRunning = RunState == RunState.Running;
        RunState = RunState.Stopped;
        PvWriting = false;
        if (wasRunning && StopPv != StopPvMode.Hold)
        {
            double v = StopPv == StopPvMode.Initial ? InitialPv : StopPvValue;
            Model.Reset(PvToModel(v));
            Pv = v;
            if (Comm == CommStatus.Ok) _pendingPvWrite = v;
        }
        log.Add(this, "運転", wasRunning
            ? $"制御停止{(reason != null ? $"（{reason}）" : "")}・停止時PV: {Labels.Of(StopPv)}"
            : "モデル単体プレビュー終了");
        return true;
    }

    public void Reset(EventLog log)
    {
        lock (Sync)
        {
            CancelDisturbanceScheduleCore(log);
            Model.Reset(PvToModel(InitialPv));
            Pv = InitialPv;
            Elapsed = 0;
            if (Comm == CommStatus.Ok) Alarm = null;
            log.Add(this, "リセット", $"初期状態へリセット（PV {InitialPv} {PvRange.Unit}）");
        }
    }

    public void SetPreview(bool on, EventLog log)
    {
        lock (Sync)
        {
            if (on && RunState == RunState.Stopped)
            {
                _pendingPvWrite = null;
                RunState = RunState.Preview;
                SetPreviewMv(Mv);
                log.Add(this, "運転", "モデル単体プレビュー開始（PLC切離し）");
            }
            else if (!on && RunState == RunState.Preview)
            {
                StopCore(log, null);
            }
        }
    }

    public void SetPreviewMv(double v)
    {
        if (!double.IsFinite(v)) return;
        lock (Sync) PreviewMv = MvOnOff ? (v >= 50 ? 100 : 0) : Math.Clamp(v, MvRange.EngMin, MvRange.EngMax);
    }

    public void SetForce(ForceKey key, bool on, double value, EventLog log)
    {
        lock (Sync)
        {
            var f = Forces[key];
            bool changed = f.On != on || (on && f.Value != value);
            f.Value = value;
            f.On = on;
            if (changed)
                log.Add(this, "FORCE", on ? $"{Labels.Of(key)} FORCE ON {value:0.0##}" : $"{Labels.Of(key)} FORCE OFF");
        }
    }

    public int ReleaseForces(EventLog log, string? reason = null)
    {
        lock (Sync)
        {
            int n = 0;
            foreach (var k in Forces.Active)
            {
                Forces[k].On = false;
                n++;
                log.Add(this, "FORCE", $"{Labels.Of(k)} FORCE OFF{(reason != null ? $"（{reason}）" : "")}");
            }
            return n;
        }
    }

    public void SetDisturbance(bool on, double t, EventLog log)
    {
        lock (Sync)
        {
            CancelDisturbanceScheduleCore(log);
            if (DisturbanceOn == on) return;
            DisturbanceOn = on;
            DisturbanceSince = t;
            log.Add(this, "外乱", on ? $"外乱投入: {Info.DistLabel} {DisturbanceValue:0.0##} {Info.DistUnit}" : "外乱解除");
        }
    }

    public void SetDisturbanceValue(double v)
    {
        if (!double.IsFinite(v)) return;
        lock (Sync) DisturbanceValue = Math.Clamp(v, 0, Info.DistMax);
    }

    public DisturbanceScheduleSnapshot GetDisturbanceSchedule()
    {
        lock (Sync) return _disturbanceSchedule.Snapshot(Elapsed);
    }

    /// <summary>単発のステップ外乱を予約する。遅延0は次の演算から、継続時間nullは解除するまで投入する。</summary>
    public bool ScheduleDisturbance(double value, double delaySeconds, double? durationSeconds, EventLog log, out string? error)
    {
        lock (Sync)
        {
            error = null;
            if (!double.IsFinite(value) || value < 0 || value > Info.DistMax)
                error = $"外乱値は 0～{Info.DistMax:G} {Info.DistUnit} で指定してください。";
            else if (!double.IsFinite(delaySeconds) || delaySeconds < 0 || delaySeconds > 86400)
                error = "開始までの時間は 0～86400 秒で指定してください。";
            else if (durationSeconds is { } duration && (!double.IsFinite(duration) || duration < SimulationEngine.Dt || duration > 86400))
                error = "継続時間は 0.1～86400 秒で指定してください。";
            if (error != null) return false;
            DisturbanceOn = false;
            _disturbanceSchedule.Schedule(Elapsed, value, delaySeconds, durationSeconds);
            string ending = durationSeconds is { } d ? $"、{d:G} 秒後に解除" : "、解除まで継続";
            log.Add(this, "外乱", $"外乱を予約: {delaySeconds:G} 秒後に {value:G} {Info.DistUnit}{ending}");
            return true;
        }
    }

    public void CancelDisturbanceSchedule(EventLog log)
    {
        lock (Sync) CancelDisturbanceScheduleCore(log);
    }

    private void CancelDisturbanceScheduleCore(EventLog? log)
    {
        if (!_disturbanceSchedule.IsScheduled) return;
        _disturbanceSchedule.Cancel();
        DisturbanceOn = false;
        log?.Add(this, "外乱", "外乱の予約・実行を取消");
    }

    private void AdvanceDisturbanceSchedule(double t, EventLog log)
    {
        var previous = _disturbanceSchedule.State;
        _disturbanceSchedule.Advance(Elapsed);
        if (_disturbanceSchedule.State == previous) return;
        if (_disturbanceSchedule.State == DisturbanceScheduleState.Active)
        {
            DisturbanceOn = true;
            DisturbanceSince = t;
            log.Add(this, "外乱", $"予約外乱を投入: {_disturbanceSchedule.Value:G} {Info.DistUnit}");
        }
        else if (_disturbanceSchedule.State == DisturbanceScheduleState.Completed)
        {
            DisturbanceOn = false;
            log.Add(this, "外乱", "予約外乱が終了");
        }
    }

    /// <summary>異常を解除する。通信異常は次の周期の応答確認後に解除し、プレビュー中は確認を保留する。</summary>
    public bool ResetAlarm(EventLog log)
    {
        lock (Sync)
        {
            if (Alarm == null && Comm == CommStatus.Ok) return false;
            if (Comm != CommStatus.Ok) _resumeRequested = true;
            else
            {
                Alarm = null;
                log.Add(this, "異常", "異常リセット");
            }
            return true;
        }
    }

    public void ClearTrend()
    {
        lock (Sync) Trend.Clear();
    }

    public void SetTrendRetention(int minutes)
    {
        int capacity = TrendBuffer.CapacityForMinutes(minutes);
        lock (Sync) Trend.Resize(capacity);
    }

    // ---- 登録内容（§5）・パラメータ（§12）・異常時動作（§14） ----

    public static ControlTarget FromConfig(TargetConfig c, int seed = 0)
    {
        var t = new ControlTarget(c.Name, c.Kind, seed);
        t.ApplySettings(c);
        lock (t.Sync)
        {
            t.Model.Reset(t.PvToModel(t.InitialPv));
            t.Pv = t.InitialPv;
            t.Sp = t._spRead = t.InternalSp;
            t.Forces[ForceKey.Pv].Value = t.InitialPv;
            t.Forces[ForceKey.Sp].Value = t.InternalSp;
        }
        return t;
    }

    public TargetConfig ToConfig()
    {
        lock (Sync)
        {
            return new TargetConfig
            {
                Name = Name, Description = Description, Kind = Kind,
                MvAddress = MvAddress, MvOnOff = MvOnOff, PvAddress = PvAddress, SpAddress = SpAddress, UseSp = UseSp,
                DataType = DataType,
                MvRange = MvRange.Clone(), PvRange = PvRange.Clone(), SpRange = SpRange.Clone(),
                InitialPv = InitialPv, InternalSp = InternalSp, StopPv = StopPv, StopPvValue = StopPvValue,
                OnCommError = OnCommError, SafeMv = SafeMv, Recover = Recover,
                Params = new Dictionary<string, double>(Model.P),
            };
        }
    }

    /// <summary>登録内容を反映する。停止中に呼ぶこと。単位・タンク高さの変更時は初期状態へ戻す。</summary>
    public void ApplySettings(TargetConfig c)
    {
        if (!ModelCatalog.ValidateParameters(Kind, c.Params, out var error))
            throw new InvalidDataException(error);
        lock (Sync)
        {
            var previousMvRange = MvRange;
            bool pvUnitsChanged = PvRange.Unit != c.PvRange.Unit;
            bool levelHeightChanged = EngineeringUnits.IsLevel(Kind)
                && EngineeringUnits.TankHeight(Kind, Model.P) != EngineeringUnits.TankHeight(Kind, c.Params);
            double mvPercent = EngineeringUnits.MvToPercent(previousMvRange, Mv);
            double previewPercent = EngineeringUnits.MvToPercent(previousMvRange, PreviewMv);
            double forcePercent = EngineeringUnits.MvToPercent(previousMvRange, Forces[ForceKey.Mv].Value);
            Name = c.Name.Trim();
            Description = c.Description.Trim();
            MvAddress = c.MvAddress.Trim().ToUpperInvariant();
            MvOnOff = c.MvOnOff;
            PvAddress = c.PvAddress.Trim().ToUpperInvariant();
            SpAddress = c.SpAddress.Trim().ToUpperInvariant();
            UseSp = c.UseSp;
            DataType = c.DataType;
            MvRange = c.MvOnOff ? new RangeDef { RawMax = 1, EngMax = 100, Unit = "%" } : c.MvRange.Clone();
            PvRange = c.PvRange.Clone();
            SpRange = c.SpRange.Clone();
            InitialPv = c.InitialPv;
            InternalSp = c.InternalSp;
            if (!UseSp) _spRead = InternalSp;
            StopPv = c.StopPv;
            StopPvValue = c.StopPvValue;
            OnCommError = c.OnCommError;
            SafeMv = c.SafeMv;
            Recover = c.Recover;
            foreach (var (key, value) in c.Params)
                Model.P[key] = value;
            Mv = EngineeringUnits.MvFromPercent(MvRange, mvPercent);
            PreviewMv = EngineeringUnits.MvFromPercent(MvRange, previewPercent);
            Forces[ForceKey.Mv].Value = EngineeringUnits.MvFromPercent(MvRange, forcePercent);
            if (previousMvRange.Unit != MvRange.Unit) Trend.Clear();
            if (pvUnitsChanged || levelHeightChanged)
                ResetStateForPvUnits();
        }
    }

    private void ResetStateForPvUnits(EventLog? log = null)
    {
        CancelDisturbanceScheduleCore(log);
        Model.Reset(PvToModel(InitialPv));
        Pv = InitialPv;
        Sp = _spRead = InternalSp;
        Elapsed = 0;
        _pendingPvWrite = null;
        Forces[ForceKey.Pv].Value = InitialPv;
        Forces[ForceKey.Sp].Value = InternalSp;
        Trend.Clear();
        log?.Add(this, "リセット", "タンク高さの変更により初期状態へ戻し、トレンド履歴を消去");
    }

    public double GetParam(string key)
    {
        lock (Sync) return Model.P[key];
    }

    /// <summary>モデルパラメータを変更する。「停止中のみ」の項目は運転中に拒否する。</summary>
    public bool SetParam(string key, double value, EventLog log, out string? error)
    {
        lock (Sync)
        {
            error = null;
            var def = Info.Params.FirstOrDefault(p => p.Key == key);
            if (def == null) { error = $"パラメータ {key} はありません。"; return false; }
            if (def.StopOnly && RunState == RunState.Running) { error = $"「{def.Label}」は停止中のみ変更できます。"; return false; }
            if (!ModelCatalog.ValidateParameter(Kind, key, value, out error)) return false;
            var candidate = new Dictionary<string, double>(Model.P) { [key] = value };
            if (!ModelCatalog.ValidateParameters(Kind, candidate, out error)) return false;
            double old = Model.P[key];
            if (old == value) return true;
            Model.P[key] = value;
            if (key == "height" && EngineeringUnits.IsMillimetres(Kind, PvRange.Unit))
                ResetStateForPvUnits(log);
            log.Add(this, "パラメータ", $"{def.Label} {old:G} → {value:G}{(def.Unit.Length > 0 ? " " + def.Unit : "")}");
            return true;
        }
    }

    public bool ResetParams(EventLog log)
    {
        lock (Sync)
        {
            if (RunState == RunState.Running) return false;
            bool heightChanged = EngineeringUnits.IsMillimetres(Kind, PvRange.Unit)
                && Model.P["height"] != Info.Params.First(p => p.Key == "height").Default;
            foreach (var p in Info.Params) Model.P[p.Key] = p.Default;
            if (heightChanged) ResetStateForPvUnits(log);
            log.Add(this, "パラメータ", "モデルパラメータを標準値に戻す");
            return true;
        }
    }

    public bool ApplyCalculatedParameters(IReadOnlyDictionary<string, double> values, EventLog log, out string? error)
    {
        lock (Sync)
        {
            error = null;
            if (RunState != RunState.Stopped) { error = "制御を停止してください。"; return false; }
            foreach (var (key, value) in values)
                if (!ModelCatalog.ValidateParameter(Kind, key, value, out error)) return false;
            var candidate = new Dictionary<string, double>(Model.P);
            foreach (var (key, value) in values) candidate[key] = value;
            if (!ModelCatalog.ValidateParameters(Kind, candidate, out error)) return false;
            bool heightChanged = EngineeringUnits.IsMillimetres(Kind, PvRange.Unit)
                && Model.P["height"] != candidate["height"];
            // 組合せを検証済みなので一括反映する。項目ごとの再検証は変更順で結果が変わる。
            foreach (var (key, value) in values)
            {
                var def = Info.Params.First(p => p.Key == key);
                double old = Model.P[key];
                Model.P[key] = value;
                if (old != value) log.Add(this, "パラメータ", $"{def.Label} {old:G} → {value:G} {def.Unit}");
            }
            if (heightChanged) ResetStateForPvUnits(log);
            log.Add(this, "パラメータ", "条件入力ウィザードの計算結果を適用");
            return true;
        }
    }

    public void SetCommBehavior(CommErrorAction onError, double safeMv, RecoverMode recover, StopPvMode stopPv, double stopPvValue, EventLog log)
    {
        lock (Sync)
        {
            if (OnCommError != onError || (onError == CommErrorAction.SafeMv && SafeMv != safeMv))
                log.Add(this, "パラメータ", $"通信異常時動作: {Labels.Of(onError)}{(onError == CommErrorAction.SafeMv ? $"（{safeMv:G} {MvRange.Unit}）" : "")}");
            if (Recover != recover)
                log.Add(this, "パラメータ", $"復旧時: {(recover == RecoverMode.Auto ? "自動再開" : "手動再開")}");
            if (StopPv != stopPv || (stopPv == StopPvMode.Value && StopPvValue != stopPvValue))
                log.Add(this, "パラメータ", $"停止時PV: {Labels.Of(stopPv)}{(stopPv == StopPvMode.Value ? $"（{stopPvValue:G} {PvRange.Unit}）" : "")}");
            OnCommError = onError;
            SafeMv = Math.Clamp(safeMv, MvRange.EngMin, MvRange.EngMax);
            Recover = recover;
            StopPv = stopPv;
            StopPvValue = stopPvValue;
        }
    }
    // ---- 周期処理（演算スレッド） ----

    public void Cycle(IPlcClient plc, double dt, double t, EventLog log)
    {
        lock (Sync)
        {
            if (RunState != RunState.Preview) PollRecovery(plc, t, log);

            if (_pendingPvWrite is double stopValue)
            {
                _pendingPvWrite = null;
                if (double.IsFinite(stopValue)) plc.Write(PvAddress, DataType, PvRange.ToRaw(stopValue));
            }

            // 1〜3. MV・SP取得と工業値変換（Pending は通信ループの初回取得待ち。異常にせず前回値を保持する）
            double mv = Mv;
            if (RunState == RunState.Running)
            {
                var mvStatus = plc.Read(MvAddress, MvDataType, out double mvRaw);
                if (mvStatus == PlcIoStatus.Failed) CommFail(MvAddress, t, log, plc);
                else if (mvStatus == PlcIoStatus.Ok && Comm != CommStatus.Ok && Recover == RecoverMode.Auto) RecoverComm(log, "自動");

                if (UseSp && Comm == CommStatus.Ok && plc.Read(SpAddress, DataType, out double spRaw) == PlcIoStatus.Ok)
                    _spRead = SpRange.ToEng(spRaw);

                if (RunState == RunState.Running && mvStatus != PlcIoStatus.Pending)
                {
                    if (Comm == CommStatus.Ok) mv = MvOnOff ? (mvRaw == 0 ? 0 : 100) : MvRange.ToEng(mvRaw);
                    else mv = OnCommError switch
                    {
                        CommErrorAction.SafeMv => SafeMv,
                        CommErrorAction.StopPvWrite when mvStatus == PlcIoStatus.Ok => MvRange.ToEng(mvRaw),
                        _ => Mv,
                    };
                }
            }
            else if (RunState == RunState.Preview)
            {
                mv = PreviewMv;
            }

            if (Forces[ForceKey.Mv].On) mv = Forces[ForceKey.Mv].Value;
            if (!double.IsFinite(mv))
            {
                RaiseAlarm("MV値異常（NaN / Infinity）", log);
                mv = Mv;
            }
            Mv = Math.Clamp(mv, MvRange.EngMin, MvRange.EngMax);
            Sp = Forces[ForceKey.Sp].On ? Forces[ForceKey.Sp].Value : UseSp ? _spRead : InternalSp;

            // 4〜6. モデル更新・PV計算・センサ特性
            if (RunState != RunState.Stopped) AdvanceDisturbanceSchedule(t, log);
            double dist = CurrentDisturbance();
            Model.InputOverride = Forces[ForceKey.ModelInput].On ? Forces[ForceKey.ModelInput].Value : null;
            if (RunState != RunState.Stopped)
            {
                double pv = Model.Step(EngineeringUnits.MvToPercent(MvRange, Mv), dist, dt, t);
                Elapsed += dt;
                if (!double.IsFinite(pv))
                {
                    RaiseAlarm("モデル演算異常（NaN / Infinity）", log);
                    Model.Reset(PvToModel(InitialPv));
                    StopCore(log, "演算異常");
                }
            }
            Pv = Forces[ForceKey.Pv].On ? Forces[ForceKey.Pv].Value : PvFromModel(Model.Output);

            // 7〜8. PLC値へ変換して書込み（異常値は書き込まない）
            PvWriting = RunState == RunState.Running
                        && !(Comm != CommStatus.Ok && OnCommError is CommErrorAction.StopPvWrite or CommErrorAction.StopSimulation);
            if (PvWriting)
            {
                if (!double.IsFinite(Pv))
                {
                    RaiseAlarm("PV値異常（NaN / Infinity）のため書込みを中止", log);
                    PvWriting = false;
                }
                else if (plc.Write(PvAddress, DataType, PvRange.ToRaw(Pv)) == PlcIoStatus.Failed)
                {
                    CommFail(PvAddress, t, log, plc);
                }
            }

            // 9. トレンド記録
            Trend.Add(new TrendSample(t, Sp, Pv, Mv, dist, Forces.Any, Comm == CommStatus.Ok));
        }
    }

    private void PollRecovery(IPlcClient plc, double t, EventLog log)
    {
        if (_resumeRequested)
        {
            var status = plc.Read(MvAddress, MvDataType, out _);
            if (status == PlcIoStatus.Pending) return; // 通信ループの取得を待ってから判定する
            _resumeRequested = false;
            if (status == PlcIoStatus.Ok) RecoverComm(log, "手動");
            else log.Add(this, "通信", $"再接続失敗：{MvAddress} 応答なし{Detail(plc)}");
        }
        else if (Comm != CommStatus.Ok && Recover == RecoverMode.Auto && RunState != RunState.Running && t - _lastPoll >= 1)
        {
            _lastPoll = t;
            if (plc.Read(MvAddress, MvDataType, out _) == PlcIoStatus.Ok) RecoverComm(log, "自動");
        }
    }

    private static string Detail(IPlcClient plc) => plc.Status.LastError is { } e ? $"（{e}）" : "";
    private void CommFail(string address, double t, EventLog log, IPlcClient plc)
    {
        if (Comm != CommStatus.Ok) return;
        Comm = CommStatus.Timeout;
        _lastPoll = t;
        Alarm = $"通信タイムアウト：{address} 応答なし{Detail(plc)}";
        log.Add(this, "異常", Alarm);
        if (OnCommError == CommErrorAction.StopSimulation) StopCore(log, "通信異常");
        else log.Add(this, "異常", $"異常時動作: {Labels.Of(OnCommError)}");
    }

    private void RecoverComm(EventLog log, string how)
    {
        Comm = CommStatus.Ok;
        Alarm = null;
        log.Add(this, "通信", $"通信復旧（{how}再開）");
    }

    private void RaiseAlarm(string message, EventLog log)
    {
        if (Alarm == message) return;
        Alarm = message;
        log.Add(this, "異常", message);
    }
}
