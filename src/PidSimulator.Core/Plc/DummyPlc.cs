namespace PidSimulator.Core.Plc;

/// <summary>
/// 実機なしで動かすためのダミーPLC。レジスタを保持し、PLC側のPID演算も模擬する。
/// </summary>
public sealed class DummyPlc : IPlcClient
{
    private readonly object _lock = new();
    private readonly Dictionary<string, double> _registers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _faults = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DummyPidLoop> _loops = [];

    public string Endpoint => "ダミーPLC";

    public PlcStatus Status => new(PlcConnectionState.Connected, Endpoint, null, 0, 0);

    public void AddLoop(DummyPidLoop loop)
    {
        lock (_lock) _loops.Add(loop);
    }

    public void ClearLoops(bool resetState = false)
    {
        lock (_lock)
        {
            _loops.Clear();
            if (resetState)
            {
                _registers.Clear();
                _faults.Clear();
            }
        }
    }

    public DummyPidLoop? FindLoop(string mvAddress)
    {
        lock (_lock) return _loops.FirstOrDefault(l => string.Equals(l.MvAddress, mvAddress, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>デモ用：指定アドレスを応答なしにする</summary>
    public void SetFault(string address, bool fault)
    {
        lock (_lock)
        {
            if (fault) _faults.Add(address);
            else _faults.Remove(address);
        }
    }

    public bool IsFaulted(string address)
    {
        lock (_lock) return _faults.Contains(address);
    }

    public bool TryRead(string address, out double raw) => Read(address, "INT16", out raw) == PlcIoStatus.Ok;

    public bool TryWrite(string address, double raw) => Write(address, "INT16", raw) == PlcIoStatus.Ok;

    public PlcIoStatus Read(string address, string dataType, out double raw)
    {
        lock (_lock)
        {
            raw = 0;
            if (_faults.Contains(address)) return PlcIoStatus.Failed;
            if (dataType == "BIT") { raw = PlcBitAddress.Read(_registers, address); return PlcIoStatus.Ok; }
            _registers.TryGetValue(address, out raw);
            return PlcIoStatus.Ok;
        }
    }

    public PlcIoStatus Write(string address, string dataType, double raw)
    {
        lock (_lock)
        {
            if (_faults.Contains(address) || !PlcDataTypes.CanWrite(dataType, raw)) return PlcIoStatus.Failed;
            if (dataType == "BIT") { PlcBitAddress.Write(_registers, address, raw != 0); return PlcIoStatus.Ok; }
            _registers[address] = PlcDataTypes.Clamp(dataType, raw);
            return PlcIoStatus.Ok;
        }
    }

    public void Tick(double dt)
    {
        lock (_lock)
        {
            foreach (var loop in _loops) loop.Step(_registers, dt);
        }
    }

    public Task<PlcTestResult> TestReadAsync(string address, string dataType, CancellationToken ct = default) =>
        Task.FromResult(Read(address, dataType, out double raw) == PlcIoStatus.Ok
            ? new PlcTestResult(true, raw, "OK", 0)
            : new PlcTestResult(false, 0, "応答なし", 0));

    public Task<PlcTestResult> TestWriteAsync(string address, string dataType, double raw, CancellationToken ct = default) =>
        Task.FromResult(Write(address, dataType, raw) == PlcIoStatus.Ok
            ? new PlcTestResult(true, PlcDataTypes.Clamp(dataType, raw), "OK", 0)
            : new PlcTestResult(false, 0, "書込み失敗", 0));

    public void Dispose() { }
}

/// <summary>PLC側PIDの模擬（PI、アンチワインドアップ付き）</summary>
public sealed class DummyPidLoop
{
    private double _integral;
    private bool _relay;
    public bool MvOnOff { get; init; }
    public string DataType { get; init; } = "INT16";

    public required string MvAddress { get; init; }
    public required string PvAddress { get; init; }
    public string? SpAddress { get; init; }
    public required RangeDef MvRange { get; init; }
    public required RangeDef PvRange { get; init; }
    public required RangeDef SpRange { get; init; }
    public double Sp { get; set; }
    public double Kp { get; set; } = 1;
    public double Ti { get; set; } = 10;
    public bool Reverse { get; set; }

    public static DummyPidLoop For(ControlTarget t, double? sp = null) => new()
    {
        MvAddress = t.MvAddress,
        MvOnOff = t.MvOnOff,
        DataType = t.DataType,
        PvAddress = t.PvAddress,
        SpAddress = t.UseSp ? t.SpAddress : null,
        MvRange = t.MvRange,
        PvRange = t.PvRange,
        SpRange = t.SpRange,
        Sp = sp ?? t.InternalSp,
        Kp = t.Info.PidKp,
        Ti = t.Info.PidTi,
        Reverse = t.Info.PidReverse,
    };

    internal void Step(Dictionary<string, double> reg, double dt)
    {
        if (SpAddress != null) reg[SpAddress] = PlcDataTypes.Clamp(DataType, SpRange.ToRaw(Sp));
        reg.TryGetValue(PvAddress, out double pvRaw);
        double pv = PvRange.ToEng(pvRaw);
        double e = (Sp - pv) / (PvRange.EngMax - PvRange.EngMin) * 100;
        if (Reverse) e = -e;
        if (MvOnOff)
        {
            if (e >= 0.5) _relay = true;
            else if (e <= -0.5) _relay = false;
            PlcBitAddress.Write(reg, MvAddress, _relay);
            return;
        }
        double integral = _integral + e * dt / Ti;
        double u = Kp * (e + integral);
        if (u is >= 0 and <= 100) _integral = integral;
        reg[MvAddress] = PlcDataTypes.Clamp(DataType, MvRange.ToRaw(EngineeringUnits.MvFromPercent(MvRange, Math.Clamp(u, 0, 100))));
    }
}
