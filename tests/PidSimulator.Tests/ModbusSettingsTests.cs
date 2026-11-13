using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public sealed class ModbusSettingsTests
{
    private static PlcSettings Settings() => new()
    {
        Mode = PlcMode.ModbusTcp, Host = "192.168.0.10", Port = 502,
    };

    [Fact]
    public void Project_RetainsModbusSettings_AndEffectiveSettingsUseTcpOnly()
    {
        var settings = Settings();
        settings.ModbusUnitId = 7;
        settings.ModbusWordOrder = ModbusWordOrder.LowWordFirst;
        settings.ModbusTestAddress = "DI15";
        settings.Udp = true;
        settings.UseGxSimulator = true;
        settings.UseKvSimulator = true;
        var restored = ProjectSerializer.FromJson(ProjectSerializer.ToJson(new ProjectDocument { Plc = settings }));
        Assert.Equal(2, restored.FormatVersion);
        Assert.True(settings.SameConnection(restored.Plc));
        Assert.Equal(7, restored.Plc.ModbusUnitId);
        Assert.Equal(ModbusWordOrder.LowWordFirst, restored.Plc.ModbusWordOrder);
        Assert.Equal("DI15", restored.Plc.ModbusTestAddress);
        var effective = restored.Plc.Effective();
        Assert.False(effective.Udp);
        Assert.False(effective.UseGxSimulator);
        Assert.False(effective.UseKvSimulator);
        Assert.Equal("192.168.0.10", effective.Host);
        Assert.Equal(502, effective.Port);
        Assert.True(restored.Plc.Udp);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public void UnitId_IsValidatedBeforeConnectionAndProjectLoad(int unitId, bool valid)
    {
        var settings = Settings();
        settings.ModbusUnitId = unitId;
        Assert.Equal(valid, settings.TryValidateModbus(out _));
        string json = ProjectSerializer.ToJson(new ProjectDocument { Plc = settings });
        if (valid)
        {
            Assert.Equal(unitId, settings.Effective().ModbusUnitId);
            Assert.Equal(unitId, ProjectSerializer.FromJson(json).Plc.ModbusUnitId);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => settings.Effective());
            Assert.Throws<InvalidDataException>(() => ProjectSerializer.FromJson(json));
        }
    }

    [Fact]
    public void InvalidWordOrderOrTestAddress_IsRejected()
    {
        var settings = Settings();
        settings.ModbusWordOrder = (ModbusWordOrder)99;
        Assert.False(settings.TryValidateModbus(out _));
        Assert.Throws<ArgumentException>(() => settings.Effective());
        settings.ModbusWordOrder = ModbusWordOrder.HighWordFirst;
        settings.ModbusTestAddress = "40001";
        Assert.False(settings.TryValidateModbus(out _));
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.FromJson(ProjectSerializer.ToJson(new ProjectDocument { Plc = settings })));
        settings.ModbusTestAddress = "IR65535.15";
        Assert.True(settings.TryValidateModbus(out _));
    }

    [Fact]
    public void ConnectionIdentity_UsesModbusFieldsAndIgnoresOtherProtocols()
    {
        var original = Settings();
        var same = original.Clone();
        same.Profile = "keyence:kv-x500";
        same.Udp = true;
        same.UseGxSimulator = same.UseKvSimulator = true;
        same.SlmpModuleIo = SlmpModuleIoTarget.MultipleCpu4;
        same.ModbusTestAddress = " ir0 ";
        Assert.True(original.SameConnection(same));
        foreach (Action<PlcSettings> change in new Action<PlcSettings>[]
        {
            s => s.ModbusUnitId = 2,
            s => s.ModbusWordOrder = ModbusWordOrder.LowWordFirst,
            s => s.ModbusTestAddress = "HR0",
            s => s.Host = "192.168.0.11",
            s => s.Port = 503,
            s => s.TimeoutMs = 2000,
            s => s.CommCycleMs = 200,
        })
        {
            var changed = original.Clone();
            change(changed);
            Assert.False(original.SameConnection(changed));
        }
        original.Mode = same.Mode = PlcMode.Slmp;
        same = original.Clone();
        same.ModbusUnitId = 2;
        same.ModbusWordOrder = ModbusWordOrder.LowWordFirst;
        same.ModbusTestAddress = "HR10";
        Assert.True(original.SameConnection(same));
    }
}
