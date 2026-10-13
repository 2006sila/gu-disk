using System.Runtime.InteropServices;
using GuDisk.Core.Interop;
using static GuDisk.Core.Interop.NativeMethods;
using static GuDisk.Core.Interop.StorageStructures;

namespace GuDisk.Core.Devices;

public sealed record ProbeLine(string Step, bool Ok, string Detail, int Win32Error = 0);

/// <summary>
/// 逐项诊断：把每个 IOCTL / SetupAPI 调用的真实结果与 Win32 错误码打出来。
/// 用途：M1 开发期定位（"为什么读不到"），后续也接进 UI 的"协议探测"面板。
/// </summary>
public static class DeviceProbe
{
    public static IReadOnlyList<ProbeLine> Run(int deviceNumber)
    {
        var list = new List<ProbeLine>();
        string path = @"\\.\PhysicalDrive" + deviceNumber;
        list.Add(new ProbeLine("目标路径", true, path));

        // ---- 三种打开方式 ----
        using (var h0 = DeviceIo.Open(path, 0))
            list.Add(h0.IsInvalid
                ? new ProbeLine("打开 access=0", false, "失败", Marshal.GetLastWin32Error())
                : new ProbeLine("打开 access=0", true, "成功（查询类 IOCTL 够用）"));

        using (var hr = DeviceIo.Open(path, GENERIC_READ))
            list.Add(hr.IsInvalid
                ? new ProbeLine("打开 GENERIC_READ", false, "失败（通常需要管理员）", Marshal.GetLastWin32Error())
                : new ProbeLine("打开 GENERIC_READ", true, "成功"));

        int rwErr = 0;
        using (var hw = DeviceIo.Open(path, GENERIC_READ | GENERIC_WRITE))
            if (hw.IsInvalid) rwErr = Marshal.GetLastWin32Error();
            else list.Add(new ProbeLine("打开 READ|WRITE", true, "成功（SMART 透传可用）"));
        if (rwErr != 0)
            list.Add(new ProbeLine("打开 READ|WRITE", false, "失败（SMART 透传需要管理员）", rwErr));

        using var h = DeviceIo.Open(path, 0);
        if (h.IsInvalid) return list;

        // ---- 设备号 ----
        {
            var buf = new byte[Marshal.SizeOf<STORAGE_DEVICE_NUMBER>()];
            bool ok = DeviceIo.Ioctl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, null, 0, buf, out int ret, out int err);
            if (ok)
            {
                var n = DeviceIo.BytesToStruct<STORAGE_DEVICE_NUMBER>(buf);
                list.Add(new ProbeLine("IOCTL_STORAGE_GET_DEVICE_NUMBER", true,
                    $"DeviceNumber={n.DeviceNumber} PartitionNumber={n.PartitionNumber} ret={ret}"));
            }
            else list.Add(new ProbeLine("IOCTL_STORAGE_GET_DEVICE_NUMBER", false, "失败", err));
        }

        // ---- 设备描述符 ----
        {
            byte[]? raw = DeviceIo.QueryProperty(h, StoragePropertyId.StorageDeviceProperty, 1024);
            if (raw is null) list.Add(new ProbeLine("StorageDeviceProperty", false, "查询失败", Marshal.GetLastWin32Error()));
            else
            {
                var dd = DeviceIo.BytesToStruct<STORAGE_DEVICE_DESCRIPTOR>(raw);
                list.Add(new ProbeLine("StorageDeviceProperty", true,
                    $"返回 {raw.Length} B · BusType={(StorageBusType)dd.BusType} · Vendor=\"{DeviceIo.ReadAnsiString(raw, dd.VendorIdOffset)}\" · " +
                    $"Product=\"{DeviceIo.ReadAnsiString(raw, dd.ProductIdOffset)}\" · SN=\"{DeviceIo.ReadAnsiString(raw, dd.SerialNumberOffset)}\""));
            }
        }

        // ---- 适配器描述符 ----
        {
            byte[]? raw = DeviceIo.QueryProperty(h, StoragePropertyId.StorageAdapterProperty, 256);
            if (raw is null) list.Add(new ProbeLine("StorageAdapterProperty", false, "查询失败", Marshal.GetLastWin32Error()));
            else
            {
                var ad = DeviceIo.BytesToStruct<STORAGE_ADAPTER_DESCRIPTOR>(raw);
                list.Add(new ProbeLine("StorageAdapterProperty", true,
                    $"BusType={ad.BusType} BusVersion={ad.BusMajorVersion}.{ad.BusMinorVersion} MaxTransfer={ad.MaximumTransferLength}"));
            }
        }

        // ---- NVMe Identify（关键：身份字段的来源）----
        {
            byte[]? id = DeviceIo.QueryProtocolSpecific(h, StoragePropertyId.StorageDeviceProtocolSpecificProperty,
                ProtocolType.Nvme, (int)NvmeDataType.Identify, 1, 4096);
            if (id is null)
                list.Add(new ProbeLine("NVMe Identify (CNS=1)", false,
                    "查询失败（非 NVMe 或驱动不支持该属性）", Marshal.GetLastWin32Error()));
            else
            {
                var idc = GuDisk.Core.Smart.NvmeIdentifyController.Parse(id);
                list.Add(new ProbeLine("NVMe Identify (CNS=1)", true,
                    idc is null ? $"返回 {id.Length} B（解析失败）"
                                : $"VID=0x{idc.VendorId:X4} Model=\"{idc.ModelNumber}\" SN=\"{idc.SerialNumber}\" FW=\"{idc.FirmwareRevision}\""));
            }
        }

        // ---- SMART / Health Log（纯探测，不需要管理员也可能失败）----
        {
            byte[]? log = DeviceIo.QueryProtocolSpecific(h, StoragePropertyId.StorageDeviceProtocolSpecificProperty,
                ProtocolType.Nvme, (int)NvmeDataType.LogPage, 0x02, 512);
            list.Add(log is null
                ? new ProbeLine("NVMe SMART/Health Log", false, "读取失败（需管理员）", Marshal.GetLastWin32Error())
                : new ProbeLine("NVMe SMART/Health Log", true, $"读到 {log.Length} B · 综合温度 {BitConverter.ToInt16(log, 1) - 273} ℃"));
        }

        // ---- SetupAPI 实例 ID ----
        {
            Guid guid = GUID_DEVINTERFACE_DISK;
            IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1))
                list.Add(new ProbeLine("SetupDiGetClassDevs", false, "失败", Marshal.GetLastWin32Error()));
            else
            {
                try
                {
                    bool found = false;
                    for (uint i = 0; i < 64; i++)
                    {
                        var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                        if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref did)) break;

                        SetupDiGetDeviceInterfaceDetailW(set, ref did, IntPtr.Zero, 0, out int required, IntPtr.Zero);
                        if (required <= 0) continue;
                        IntPtr buf = Marshal.AllocHGlobal(required);
                        var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                        try
                        {
                            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                            if (!SetupDiGetDeviceInterfaceDetailW(set, ref did, buf, required, out _, ref info))
                            {
                                list.Add(new ProbeLine($"接口 #{i} 取详情", false, "失败", Marshal.GetLastWin32Error()));
                                continue;
                            }
                            string ifacePath = Marshal.PtrToStringUni(IntPtr.Add(buf, 4)) ?? "";
                            if (ifacePath.Length == 0) continue;

                            using var hh = DeviceIo.Open(ifacePath, 0);
                            if (hh.IsInvalid) continue;
                            if (DeviceIo.GetDeviceNumber(hh) != (uint)deviceNumber) continue;

                            var sb = new char[512];
                            bool okId = SetupDiGetDeviceInstanceIdW(set, ref info, sb, sb.Length, out int req);
                            int len = Array.IndexOf(sb, '\0');
                            string iid = new string(sb, 0, len < 0 ? 0 : len);
                            list.Add(new ProbeLine("SetupDiGetDeviceInstanceId", okId && iid.Length > 0,
                                okId ? (iid.Length > 0 ? iid : "返回空串") : "调用失败",
                                okId ? 0 : Marshal.GetLastWin32Error()));
                            list.Add(new ProbeLine("DEVINST", info.DevInst != 0, "0x" + info.DevInst.ToString("X8")));

                            string adapter = UsbBridgeDetector.FindAdapter(info.DevInst);
                            list.Add(new ProbeLine("向上找 PCI 控制器", adapter.Length > 0, adapter.Length > 0 ? adapter : "未找到"));

                            var usb = UsbBridgeDetector.Walk(info.DevInst);
                            list.Add(usb is null
                                ? new ProbeLine("向上找 USB 桥接", true, "不是 USB 设备（直连）")
                                : new ProbeLine("向上找 USB 桥接", true,
                                    $"VID=0x{usb.Vid:X4} PID=0x{usb.Pid:X4} 自报=\"{usb.BusReported}\""));

                            found = true;
                            break;
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                    if (!found) list.Add(new ProbeLine("SetupDi 定位本设备", false, "没在接口列表里找到该设备号"));
                }
                finally { SetupDiDestroyDeviceInfoList(set); }
            }
        }

        return list;
    }
}
