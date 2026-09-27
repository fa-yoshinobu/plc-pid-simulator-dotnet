namespace PidSimulator.Core.Project;

/// <summary>SLMPの要求先CPU。プロジェクトには名前を保存し、通信時にModule I/O番号へ変換する。</summary>
public enum SlmpModuleIoTarget
{
    OwnStation,
    ControlSystemCpu,
    StandbySystemCpu,
    SystemACpu,
    SystemBCpu,
    MultipleCpu1,
    MultipleCpu2,
    MultipleCpu3,
    MultipleCpu4,
}
