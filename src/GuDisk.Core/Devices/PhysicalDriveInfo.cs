namespace GuDisk.Core.Devices;

public enum BusKind { Unknown, Ata, Sata, Nvme, Usb, Scsi, Raid, Virtual }

public enum StorageProtocolKind { Unknown, Ata, Sata, Nvme, Scsi }

/// <summary>桥接主控类别（决定协议梯与 quirk），见 docs/实施方案.md 4.4。</summary>
public enum UsbBridgeKind
{
    None, Sat, JmicronSata, JmicronsNvme, Jmb39x,
    AsmediaSata, AsmediaNvme, AsmediaDual,
    RealtekNvme, Sunplus, Prolific, Cypress, Iodata, Logitec, Unknown
}

/// <summary>一块物理磁盘的识别结果。</summary>
public sealed record PhysicalDriveInfo(
    int DeviceNumber,
    string DevicePath,          // \\.\\PhysicalDriveN —— SMART 透传与 smartctl 都用它
    string InterfacePath,       // SetupAPI 设备接口路径
    string InstanceId,          // 设备实例 ID（用于向上追溯控制器 / USB 桥接）
    string Model,
    string Serial,
    string Firmware,
    BusKind Bus,
    StorageProtocolKind Protocol,
    string AdapterName,
    ushort? UsbVendorId,
    ushort? UsbProductId,
    UsbBridgeKind Bridge,
    string? BridgeName,
    string? BridgeEvidence,
    string? SmartctlDeviceType,  // 建议的 smartctl -d 参数
    ulong CapacityBytes,
    uint BytesPerSector,
    bool IsRemovable,
    bool IsSystemDisk,
    IReadOnlyList<string> MountPoints,
    bool? NvmeTrim = null)   // NVMe 的 Dataset Management 能力位（ATA 盘为 null）
{
    /// <summary>持久化主键：设备号会随热插拔漂移，禁止用 DeviceNumber 做键。</summary>
    public string DiskKey => Core.DiskKey.From(Model, Serial);

    public string CapacityText => Core.Decode.UnitConverter.FormatBytes(CapacityBytes);

    public string BusText => Bus switch
    {
        BusKind.Nvme => "NVMe",
        BusKind.Sata => "SATA",
        BusKind.Ata => "ATA",
        BusKind.Usb => "USB",
        BusKind.Scsi => "SCSI",
        BusKind.Raid => "RAID",
        BusKind.Virtual => "虚拟",
        _ => "未知"
    };

    /// <summary>是否走 USB 桥接（SMART 能不能读到，全看这一条）。</summary>
    public bool IsUsb => Bus == BusKind.Usb || UsbVendorId is not null;
}
