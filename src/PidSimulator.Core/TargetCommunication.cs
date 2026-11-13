using PidSimulator.Core.Plc;

namespace PidSimulator.Core;

/// <summary>対象ごとの通信異常と復帰要求。ControlTarget.Sync のロック内で呼ぶ。</summary>
internal sealed class TargetCommunication(ControlTarget target)
{
    private bool _resumeRequested;
    private double _lastPoll;

    public CommStatus Status { get; private set; } = CommStatus.Ok;

    public void RequestRecovery() => _resumeRequested = true;
    public void CancelRecovery() => _resumeRequested = false;

    public void PollRecovery(IPlcClient plc, double t, EventLog log)
    {
        if (_resumeRequested)
        {
            var status = plc.Read(target.MvAddress, target.MvDataType, out _);
            if (status == PlcIoStatus.Pending) return;
            _resumeRequested = false;
            if (status == PlcIoStatus.Ok) Recover(log, "手動");
            else log.Add(target, "通信", $"再接続失敗：{target.MvAddress} 応答なし{Detail(plc)}");
        }
        else if (Status != CommStatus.Ok && target.Recover == RecoverMode.Auto && target.RunState != RunState.Running && t - _lastPoll >= 1)
        {
            _lastPoll = t;
            if (plc.Read(target.MvAddress, target.MvDataType, out _) == PlcIoStatus.Ok) Recover(log, "自動");
        }
    }

    /// <returns>今回の異常でモデルの停止も必要な場合に true。</returns>
    public bool Fail(string address, double t, EventLog log, IPlcClient plc)
    {
        if (Status != CommStatus.Ok) return false;
        Status = CommStatus.Timeout;
        _lastPoll = t;
        target.Alarm = $"通信タイムアウト：{address} 応答なし{Detail(plc)}";
        log.Add(target, "異常", target.Alarm);
        if (target.OnCommError == CommErrorAction.StopSimulation) return true;
        log.Add(target, "異常", $"異常時動作: {Labels.Of(target.OnCommError)}");
        return false;
    }

    public void Recover(EventLog log, string how)
    {
        Status = CommStatus.Ok;
        target.Alarm = null;
        log.Add(target, "通信", $"通信復旧（{how}再開）");
    }

    private static string Detail(IPlcClient plc) => plc.Status.LastError is { } error ? $"（{error}）" : "";
}
