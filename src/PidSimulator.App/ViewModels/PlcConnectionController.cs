using PidSimulator.Core;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;
using PidSimulator.Plc.HostLink;
using PidSimulator.Plc.Slmp;
using PidSimulator.Plc.Modbus;

namespace PidSimulator.App.ViewModels;

internal sealed class PlcConnectionController(
    SimulationEngine engine, DummyPlc dummy, IUserInteraction interaction,
    Action cancelPendingStart, Func<PlcSettings, IPlcClient>? clientFactory)
{
    public PlcSettings Settings { get; private set; } = new();

    private IPlcClient CreateClient(PlcSettings settings) => clientFactory?.Invoke(settings) ?? (settings.Mode switch
    {
        PlcMode.Slmp => new SlmpPlcClient(settings),
        PlcMode.HostLink => new HostLinkPlcClient(settings),
        PlcMode.ModbusTcp => new ModbusPlcClient(settings),
        _ => dummy,
    });

    public bool Apply(PlcSettings next, bool force)
    {
        if (!force && next.SameConnection(Settings))
        {
            Settings = next;
            return true;
        }
        cancelPendingStart();
        if (engine.Targets.Any(t => t.RunState != RunState.Stopped))
        {
            if (!interaction.Confirm("PLC通信の切替", "制御中またはプレビュー中の対象があります。すべて停止してから通信方式を切り替えます。", "停止して切り替える", danger: true))
                return false;
            engine.StopAll("PLC通信の切替");
        }

        IPlcClient client;
        try { client = CreateClient(next); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            interaction.Error("通信設定を保存できません", $"接続設定を確認してください。\n\n{ex.Message}");
            return false;
        }
        Settings = next;
        var old = engine.SwapPlc(client);
        if (!ReferenceEquals(old, client) && old is not DummyPlc) _ = Task.Run(old.Dispose);
        return true;
    }

    public bool Connect()
    {
        if (engine.Plc is not IConnectablePlcClient client) return false;
        client.Connect();
        return true;
    }

    public async Task DisconnectAsync()
    {
        cancelPendingStart();
        engine.StopAll("通信切断");
        var old = engine.SwapPlc(CreateClient(Settings));
        await Task.Run(old.Dispose);
    }
}
