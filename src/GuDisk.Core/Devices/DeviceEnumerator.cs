using System.Runtime.InteropServices;
using GuDisk.Core.Interop;
using static GuDisk.Core.Interop.NativeMethods;
using static GuDisk.Core.Interop.StorageStructures;

namespace GuDisk.Core.Devices;

/// <summary>
/// 物理磁盘枚举：SetupAPI 拿设备接口与 DEVINST → IOCTL 取设备号 / 身份 / 几何 → 判定协议 → USB 桥接识别 → 盘符与系统盘标记。
/// 查询类 IOCTL 用 access=0 打开，不需要管理员；SMART 透传才需要提权。
/// </summary>
public sealed class DeviceEnumerator
{
    public IReadOnlyList<PhysicalDriveInfo> Enumerate()
    {
        var letters = BuildDriveLetterMap();
        var result = new List<PhysicalDriveInfo>();
        var seen = new HashSet<int>();

        Guid guid = GUID_DEVINTERFACE_DISK;
        IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

        try
        {
            for (uint i = 0; ; i++)
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref did)) break;

                var (path, devInst, info) = GetInterfaceDetail(set, ref did);
                if (path.Length == 0) continue;
                string instanceId = GetInstanceId(set, ref info);

                using var h = DeviceIo.Open(path, 0);
                if (h.IsInvalid) continue;

                uint? devNum = DeviceIo.GetDeviceNumber(h);
                if (devNum is null || !seen.Add((int)devNum.Value)) continue;

                var drive = Build(h, (int)devNum.Value, path, devInst, instanceId, letters);
                if (drive is not null) result.Add(drive);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        // **给序列号可疑的 USB 盘补真实身份**。
        //
        // 为什么必须在枚举阶段做：DiskKey = SHA256(型号|序列号)。USB 桥接器经常不转发真序列号，
        // Windows 给的是 "000000000203" 这类占位串 —— 两块同型号盘拿到**完全相同**的假值，
        // DiskKey 也就一模一样，历史目录 / 告警 / 悬浮窗勾选全部串在一起（实测两块 WD20EURS）。
        // 只对"USB 且序列号看起来是假值"的盘额外跑一次 smartctl -i，普通盘不受影响。
        var suspicious = result.Where(d => d.IsUsb && DiskKey.LooksFakeSerial(d.Serial)).ToList();
        if (suspicious.Count > 0)
        {
            var engine = new Smart.SmartctlEngine();
            if (engine.Available)
                for (int i = 0; i < result.Count; i++)
                {
                    var d = result[i];
                    if (!d.IsUsb || !DiskKey.LooksFakeSerial(d.Serial)) continue;
                    try
                    {
                        if (engine.ReadIdentity(d) is not { } real) continue;
                        if (DiskKey.LooksFakeSerial(real.Serial)) continue;   // 还是假值，放弃
                        result[i] = d with
                        {
                            Model = real.Model.Length > 0 ? real.Model : d.Model,
                            Serial = real.Serial,
                            Firmware = real.Firmware.Length > 0 ? real.Firmware : d.Firmware,
                        };
                    }
                    catch { /* 补不到就保持原样，不影响枚举 */ }
                }
        }

        return result.OrderBy(d => d.DeviceNumber).ToList();
    }

    private static PhysicalDriveInfo? Build(Microsoft.Win32.SafeHandles.SafeFileHandle h, int devNum,
        string path, uint devInst, string instanceId, Dictionary<int, List<string>> letters)
    {
        byte[]? raw = DeviceIo.QueryProperty(h, StoragePropertyId.StorageDeviceProperty, 1024);
        if (raw is null) return null;
        var dd = DeviceIo.BytesToStruct<STORAGE_DEVICE_DESCRIPTOR>(raw);

        string vendor = DeviceIo.ReadAnsiString(raw, dd.VendorIdOffset);
        string product = DeviceIo.ReadAnsiString(raw, dd.ProductIdOffset);
        string revision = DeviceIo.ReadAnsiString(raw, dd.ProductRevisionOffset);
        string serial = DeviceIo.ReadAnsiString(raw, dd.SerialNumberOffset);

        string model = (vendor + " " + product).Trim();
        if (model.Length == 0) model = product.Length > 0 ? product : vendor;

        var bus = (StorageBusType)dd.BusType;
        var protocol = DetectProtocol(h, bus);
        var (size, sector) = DeviceIo.GetGeometry(h) ?? (0UL, 512u);

        // NVMe 的身份字段以 Identify Controller 为准：
        // Windows 在 STORAGE_DEVICE_DESCRIPTOR 里给的 NVMe 序列号是"ASCII 的十六进制分组"，不可直接使用。
        bool? nvmeTrim = null;   // 提升出 if 块：下面构造 PhysicalDriveInfo 要用
        if (protocol == StorageProtocolKind.Nvme)
        {
            byte[]? idRaw = DeviceIo.QueryProtocolSpecific(h, StoragePropertyId.StorageDeviceProtocolSpecificProperty,
                ProtocolType.Nvme, (int)NvmeDataType.Identify, 1, 4096);
            if (idRaw is not null && GuDisk.Core.Smart.NvmeIdentifyController.Parse(idRaw) is { } idc)
            {
                if (idc.ModelNumber.Length > 0) model = idc.ModelNumber;
                if (idc.SerialNumber.Length > 0) serial = idc.SerialNumber;
                if (idc.FirmwareRevision.Length > 0) revision = idc.FirmwareRevision;
                nvmeTrim = idc.SupportsDatasetManagement;
            }
        }

        string adapter = UsbBridgeDetector.FindAdapter(devInst);

        ushort? vid = null, pid = null;
        UsbBridgeKind bridgeKind = UsbBridgeKind.None;
        string? bridgeName = null, bridgeEvidence = null, smartctlType = null;

        if (bus == StorageBusType.Usb || bus == StorageBusType.Scsi || bus == StorageBusType.Raid)
        {
            var usb = UsbBridgeDetector.Walk(devInst);
            if (usb is not null)
            {
                vid = usb.Vid; pid = usb.Pid;
                var id = UsbBridgeDatabase.Resolve(usb.Vid, usb.Pid, usb.BusReported, null);
                bridgeKind = id.Kind; bridgeName = id.DisplayName; bridgeEvidence = id.Evidence;
                smartctlType = id.ProtocolHint;
            }
        }
        else
        {
            smartctlType = protocol switch
            {
                StorageProtocolKind.Nvme => "nvme",
                StorageProtocolKind.Ata or StorageProtocolKind.Sata => "sat",
                StorageProtocolKind.Scsi => "scsi",
                _ => "auto"
            };
        }
        smartctlType ??= "auto";

        List<string> mounts = letters.TryGetValue(devNum, out var ls) ? ls : new List<string>();
        string sysRoot = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        bool system = mounts.Contains(sysRoot);

        return new PhysicalDriveInfo(
            DeviceNumber: devNum,
            DevicePath: @"\\.\PhysicalDrive" + devNum,
            InterfacePath: path,
            InstanceId: instanceId,
            Model: model,
            Serial: serial,
            Firmware: revision,
            Bus: MapBus(bus),
            Protocol: protocol,
            AdapterName: adapter,
            UsbVendorId: vid,
            UsbProductId: pid,
            Bridge: bridgeKind,
            BridgeName: bridgeName,
            BridgeEvidence: bridgeEvidence,
            SmartctlDeviceType: smartctlType,
            CapacityBytes: size,
            BytesPerSector: sector,
            IsRemovable: dd.RemovableMedia != 0,
            IsSystemDisk: system,
            MountPoints: mounts,
            NvmeTrim: nvmeTrim);
    }

    private static BusKind MapBus(StorageBusType b) => b switch
    {
        StorageBusType.Nvme => BusKind.Nvme,
        StorageBusType.Sata => BusKind.Sata,
        StorageBusType.Ata or StorageBusType.Atapi => BusKind.Ata,
        StorageBusType.Usb => BusKind.Usb,
        StorageBusType.Scsi or StorageBusType.Sas or StorageBusType.IScsi => BusKind.Scsi,
        StorageBusType.Raid => BusKind.Raid,
        StorageBusType.Virtual or StorageBusType.FileBackedVirtual or StorageBusType.Spaces => BusKind.Virtual,
        _ => BusKind.Unknown
    };

    /// <summary>协议判定：先试 NVMe Identify，再按总线类型兜底。</summary>
    private static StorageProtocolKind DetectProtocol(Microsoft.Win32.SafeHandles.SafeFileHandle h, StorageBusType bus)
    {
        if (DeviceIo.QueryProtocolSpecific(h, StoragePropertyId.StorageDeviceProtocolSpecificProperty,
                ProtocolType.Nvme, (int)NvmeDataType.Identify, 1, 4096) is not null)
            return StorageProtocolKind.Nvme;

        if (bus == StorageBusType.Usb || bus == StorageBusType.Scsi)
            return StorageProtocolKind.Scsi;   // 桥接后面是什么得靠 SMART 阶段再探

        return bus switch
        {
            StorageBusType.Sata => StorageProtocolKind.Sata,
            StorageBusType.Ata or StorageBusType.Atapi => StorageProtocolKind.Ata,
            StorageBusType.Nvme => StorageProtocolKind.Nvme,
            StorageBusType.Raid => StorageProtocolKind.Scsi,
            _ => StorageProtocolKind.Unknown
        };
    }

    private static Dictionary<int, List<string>> BuildDriveLetterMap()
    {
        var map = new Dictionary<int, List<string>>();
        for (char c = 'A'; c <= 'Z'; c++)
        {
            string vol = @"\\.\" + c + ":";
            using var h = DeviceIo.Open(vol, 0);
            if (h.IsInvalid) continue;
            uint? n = DeviceIo.GetDeviceNumber(h);
            if (n is null) continue;
            if (!map.TryGetValue((int)n.Value, out var list)) map[(int)n.Value] = list = new List<string>();
            list.Add(c + ":");
        }
        return map;
    }

    /// <summary>一次调用同时拿到接口路径与 DEVINST —— 后续向上追溯控制器 / USB 都用 DEVINST，不再用实例 ID 反查。</summary>
    private static (string Path, uint DevInst, SP_DEVINFO_DATA Info) GetInterfaceDetail(
        IntPtr set, ref SP_DEVICE_INTERFACE_DATA did)
    {
        SetupDiGetDeviceInterfaceDetailW(set, ref did, IntPtr.Zero, 0, out int required, IntPtr.Zero);
        if (required <= 0) return ("", 0, default);

        IntPtr buf = Marshal.AllocHGlobal(required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize：x64 为 8，x86 为 6
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref did, buf, required, out _, ref info))
                return ("", 0, default);
            string p = Marshal.PtrToStringUni(IntPtr.Add(buf, 4)) ?? "";
            return (p, info.DevInst, info);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string GetInstanceId(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var buf = new char[512];
        if (!SetupDiGetDeviceInstanceIdW(set, ref info, buf, buf.Length, out _)) return "";
        int len = Array.IndexOf(buf, '\0');
        return new string(buf, 0, len < 0 ? 0 : len);
    }
}
