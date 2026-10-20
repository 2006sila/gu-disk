using GuDisk.Core.Devices;

namespace GuDisk.Core.Smart;

/// <summary>SMART 数据是怎么读出来的（UI 上要显示"本次由谁读出"）。</summary>
public enum SmartTransportKind
{
    None,
    AtaPassThroughDirect,      // IOCTL_ATA_PASS_THROUGH_DIRECT（直连）
    ScsiAtaPassThrough16,      // SCSI CDB 0x85（USB-SATA 盒 / RAID 接管）
    ScsiAtaPassThrough12,      // SCSI CDB 0xA1（老桥接）
    NvmeProtocolCommand,       // IOCTL_STORAGE_PROTOCOL_COMMAND（NVMe 直连）
    NvmeVendorTunnel,          // USB-NVMe 盒厂商隧道
    Smartctl                   // 层 2 兜底
}

/// <summary>一次协议尝试的记录 —— "盒子不透传"这类结论必须靠它说话。</summary>
public sealed record ProbeAttempt(
    string Protocol,
    string Command,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    int ElapsedMs);

public sealed record SmartReadOptions(
    string ProtocolPreference = "auto",
    bool AllowQuirkProbe = true,
    int TimeoutMs = 4000,
    bool PreferNative = true);

public sealed record IdentityInfo(
    string Model,
    string Serial,
    string Firmware,
    string Standard,
    string Interface,
    string TransferMode,
    string SupportedMode,
    int? RotationRateRpm,
    long PowerOnHours,
    long PowerOnCount,
    bool TrimSupported,
    bool ZonedDevice,
    bool NcQSupported,
    string? Capacities,
    string FormFactor = "未报告");

/// <summary>ATA 属性（SATA 盘）。</summary>
public sealed record SmartAttribute(
    byte Id,
    string NameCn,
    string NameEn,
    int Current,
    int Worst,
    int Threshold,
    ulong Raw,
    string RawHex,
    string RawMeaning,
    string Status,     // 正常 / 注意 / 危险 / 统计
    string Category)   // 坏道 / 寿命 / 环境 / 接口 / 统计
{
    /// <summary>
    /// 是否有"当前值/最差值/临界值"这套阈值体系。
    /// ATA 属性有（CDI 的三列就是它）；**NVMe 属性没有** —— 它的这三个字段恒为 0，
    /// 直接显示 0 会让人误以为"临界值是 0"，所以统一转成 "-"。
    /// </summary>
    public bool HasThresholds => Current != 0 || Worst != 0 || Threshold != 0;

    public string CurrentText => HasThresholds ? Current.ToString() : "-";
    public string WorstText => HasThresholds ? Worst.ToString() : "-";
    public string ThresholdText => HasThresholds ? Threshold.ToString() : "-";
}

public sealed record AtaSmartBlock(
    bool SmartPassed,
    IReadOnlyList<SmartAttribute> Attributes,
    byte[] Raw512);

/// <summary>NVMe SMART / Health Information Log（512 B）的解码结果。</summary>
public sealed record NvmeSmartLog(
    byte CriticalWarning,
    short CompositeTemperatureK,
    byte AvailableSpare,
    byte AvailableSpareThreshold,
    byte PercentageUsed,
    ulong DataUnitsRead,
    ulong DataUnitsWritten,
    ulong HostReadCommands,
    ulong HostWriteCommands,
    ulong ControllerBusyTime,
    ulong PowerCycles,
    ulong PowerOnHours,
    ulong UnsafeShutdowns,
    ulong MediaErrors,
    ulong ErrorLogEntries,
    uint WarningTempTimeMinutes,
    uint CriticalTempTimeMinutes,
    IReadOnlyList<short> TemperatureSensors,
    uint ThermalTransition1,
    uint ThermalTransition2,
    uint ThermalTime1Minutes,
    uint ThermalTime2Minutes,
    byte[] Raw512)
{
    public double? CompositeTemperatureC => CompositeTemperatureK > 0 ? CompositeTemperatureK - 273.15 : null;

    /// <summary>严重警告位（NVMe 规范位定义）。</summary>
    public IReadOnlyList<string> CriticalWarningFlags()
    {
        var f = new List<string>();
        if ((CriticalWarning & 0x01) != 0) f.Add("可用备用空间低于阈值");
        if ((CriticalWarning & 0x02) != 0) f.Add("温度超过或低于复合温度阈值");
        if ((CriticalWarning & 0x04) != 0) f.Add("NVM 子系统可靠性下降");
        if ((CriticalWarning & 0x08) != 0) f.Add("介质已置为只读");
        if ((CriticalWarning & 0x10) != 0) f.Add("易失性内存备份失败");
        return f;
    }
}

public sealed record SmartReadResult(
    bool Success,
    string Engine,
    SmartTransportKind UsedTransport,
    string? ErrorCode,
    string? ErrorMessage,
    AtaSmartBlock? Ata,
    NvmeSmartLog? Nvme,
    IdentityInfo? Identity,
    IReadOnlyList<ProbeAttempt> Attempts)
{
    public static SmartReadResult Fail(string engine, string code, string message,
        IReadOnlyList<ProbeAttempt>? attempts = null)
        => new(false, engine, SmartTransportKind.None, code, message, null, null, null, attempts ?? []);
}

/// <summary>层 1 / 层 2 的统一接口（M1 实现 NativeSmartEngine 与 SmartctlEngine）。</summary>
public interface ISmartEngine
{
    string Name { get; }
    SmartReadResult Read(PhysicalDriveInfo drive, SmartReadOptions options);
}
