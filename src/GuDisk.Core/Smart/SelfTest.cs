using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using GuDisk.Core.Devices;
using GuDisk.Core.Interop;
using static GuDisk.Core.Interop.AtaStructures;
using static GuDisk.Core.Interop.StorageStructures;

namespace GuDisk.Core.Smart;

public enum SelfTestKind { Short, Extended, Conveyance, Abort }

public sealed record SelfTestResult(bool Success, string? Error, int Win32Error, string Message, string Transport);

public sealed record SelfTestLogEntry(int Number, string Type, string Status, int RemainingPercent,
    long PowerOnHours, long Lba);

public sealed record ErrorLogEntry(int Number, long ErrorCode, long Lba, bool Uncorrectable, string Raw);

/// <summary>
/// 硬件自检与日志读取。
///
/// ⚠️ **需要管理员权限**，且 ATA 路径还需要 SATA 盘才能真机验证 —— 本机只有 NVMe 且未提权，
/// 因此这几条命令**只验证了错误码路径合理**（Win32 5 / 不支持），实机结论待补。
///
/// 命令依据：ATA SMART EXECUTE OFF-LINE IMMEDIATE = 0xB0/FEATURES 0xD4/LBA Low 选子命令
/// （0x01 短、0x02 扩展、0x7F 中止）；NVMe Device Self-test = Admin 命令 0x14，DW10 低 4 位为自检码（1/2/0xF）。
/// </summary>
public static class SelfTest
{
    public static SelfTestResult Start(PhysicalDriveInfo drive, SelfTestKind kind)
    {
        using var h = DeviceIo.Open(drive.DevicePath, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            return new SelfTestResult(false, err == 5 ? "自检需要管理员权限（Win32 5）" : "打开设备失败（Win32 " + err + "）",
                err, "", "");
        }

        if (drive.Protocol == StorageProtocolKind.Nvme && !drive.IsUsb)
            return NvmeSelfTest(h, kind);

        return AtaSelfTest(h, kind);
    }

    // ---------------- ATA ----------------
    private static SelfTestResult AtaSelfTest(SafeFileHandle h, SelfTestKind kind)
    {
        byte sub = kind switch
        {
            SelfTestKind.Short => SMART_SHORT_SELFTEST,
            SelfTestKind.Extended => SMART_EXTENDED_SELFTEST,
            SelfTestKind.Abort => SMART_ABORT_SELFTEST,
            _ => SMART_SHORT_SELFTEST
        };
        var task = DeviceIoAta.SmartTask(ATA_SMART_EXECUTE_OFFLINE, lbaLow: sub, lbaMid: 0x00, sectorCount: 0x00);
        bool ok = DeviceIoAta.AtaPassThrough(h, new DeviceIoAta.AtaCommand(task, false, 0), null, out _, out int err);

        string label = kind switch
        {
            SelfTestKind.Short => "短自检（约 2 分钟）",
            SelfTestKind.Extended => "扩展自检（可能数小时）",
            SelfTestKind.Abort => "中止自检",
            _ => "自检"
        };
        return ok
            ? new SelfTestResult(true, null, 0, "已下发自检命令：" + label + "。可用 selftest-log 查看进度。", "IOCTL_ATA_PASS_THROUGH_DIRECT")
            : new SelfTestResult(false, "命令失败（Win32 " + err + "）", err, "", "IOCTL_ATA_PASS_THROUGH_DIRECT");
    }

    // ---------------- NVMe ----------------
    private static SelfTestResult NvmeSelfTest(SafeFileHandle h, SelfTestKind kind)
    {
        byte code = kind switch
        {
            SelfTestKind.Short => 0x01,
            SelfTestKind.Extended => 0x02,
            SelfTestKind.Abort => 0x0F,
            _ => 0x01
        };
        // NVMe Device Self-test：opcode 0x14，DW10 位 3:0 = 自检码
        var cmd = new byte[64];
        cmd[0] = 0x14;
        cmd[40] = code;                    // DW10 低字节（命令从 DW0 起算，DW10 在第 40 字节）

        bool ok = DeviceIo.SendNvmeCommand(h, cmd, 0, out int status, out int err, timeoutSeconds: 10);
        string label = code switch { 1 => "短自检", 2 => "扩展自检", 0x0F => "中止自检", _ => "自检" };
        return ok && status == 0
            ? new SelfTestResult(true, null, 0, "已下发自检命令：" + label + "。可用 selftest-log 查看进度。", "IOCTL_STORAGE_PROTOCOL_COMMAND")
            : new SelfTestResult(false, ok ? "NVMe 命令返回状态 0x" + status.ToString("X") : "命令失败（Win32 " + err + "）",
                err, "", "IOCTL_STORAGE_PROTOCOL_COMMAND");
    }

    // ---------------- 自检日志 ----------------
    /// <summary>读自检日志。ATA 走 SMART READ LOG 0x06；NVMe 走 Get Log Page 0x06。</summary>
    public static (bool Ok, string? Error, IReadOnlyList<SelfTestLogEntry> Entries, string Raw) ReadSelfTestLog(
        PhysicalDriveInfo drive)
    {
        using var h = DeviceIo.Open(drive.DevicePath, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            return (false, err == 5 ? "读取日志需要管理员权限（Win32 5）" : "打开设备失败（Win32 " + err + "）",
                [], "");
        }

        if (drive.Protocol == StorageProtocolKind.Nvme && !drive.IsUsb)
        {
            byte[]? log = DeviceIo.QueryProtocolSpecific(h, StoragePropertyId.StorageDeviceProtocolSpecificProperty,
                ProtocolType.Nvme, (int)NvmeDataType.LogPage, 0x06, 564);
            if (log is null) return (false, "读取 NVMe 自检日志失败（可能需要管理员）", [], "");
            return (true, null, ParseNvmeSelfTest(log), BitConverter.ToString(log));
        }

        var task = DeviceIoAta.SmartTask(ATA_SMART_READ_LOG, lbaLow: 0x06, lbaMid: 0x00, sectorCount: 1);
        if (!DeviceIoAta.AtaPassThrough(h, new DeviceIoAta.AtaCommand(task, true, 512), null, out var data, out int e2)
            || data is null)
            return (false, "读取 ATA 自检日志失败（Win32 " + e2 + "）", [], "");

        return (true, null, ParseAtaSelfTestLog(data), BitConverter.ToString(data));
    }

    private static IReadOnlyList<SelfTestLogEntry> ParseAtaSelfTestLog(byte[] d)
    {
        var list = new List<SelfTestLogEntry>();
        for (int i = 0; i < 21 && 2 + i * 24 + 24 <= d.Length; i++)
        {
            int o = 2 + i * 24;
            byte num = d[o], type = d[o + 1], status = d[o + 2];
            if (num == 0 && type == 0 && status == 0) continue;
            list.Add(new SelfTestLogEntry(num, AtaTestType(type), AtaTestStatus(status), d[o + 3],
                d[o + 4] | (d[o + 5] << 8) | (d[o + 6] << 16) | (d[o + 7] << 24),
                d[o + 8] | (d[o + 9] << 8) | (d[o + 10] << 16) | (d[o + 11] << 24)));
        }
        return list;
    }

    private static string AtaTestType(byte t) => (t & 0x7F) switch
    {
        0 => "离线", 1 => "短自检", 2 => "扩展自检", 3 => "传输自检", 4 => "选择性自检", _ => "未知(0x" + t.ToString("X2") + ")"
    };

    private static string AtaTestStatus(byte s) => s switch
    {
        0x00 => "已完成且无错误", 0x01 => "已中止", 0x02 => "被主机中断", 0x03 => "致命错误",
        0x04 => "未知错误", 0x05 => "电气元件错误", 0x06 => "伺服错误", 0x07 => "读取错误", 0x08 => "正在处理",
        0x09 => "已中止（离线扫描）", 0x0F => "已完成且发现错误", _ => "未知(0x" + s.ToString("X2") + ")"
    };

    private static IReadOnlyList<SelfTestLogEntry> ParseNvmeSelfTest(byte[] d)
    {
        var list = new List<SelfTestLogEntry>();
        // 字节 0 = 当前操作类型，1-2 = 当前进度（0-100），之后 20 条记录、每条 16 字节
        for (int i = 0; i < 20; i++)
        {
            int o = 4 + i * 16;
            if (o + 16 > d.Length) break;
            byte status = (byte)(d[o + 12] & 0x0F);
            if (status == 0x0F) continue;                       // 未使用
            byte type = (byte)(d[o + 12] >> 4);
            list.Add(new SelfTestLogEntry(i + 1,
                type switch { 1 => "短自检", 2 => "扩展自检", 3 => "厂商自检", _ => "未知" },
                status switch { 0 => "已完成且无错误", 1 => "已中止", 2 => "被主机中断", 4 => "致命错误",
                                5 => "未知错误", 6 => "电气元件错误", 7 => "伺服错误", 8 => "读取错误",
                                9 => "正在处理", 0xA => "已完成且发现错误", _ => "未知(0x" + status.ToString("X") + ")" },
                0, (long)(BitConverter.ToUInt64(d, o) & 0xFFFF), 0));
        }
        return list;
    }

    // ---------------- 错误日志 ----------------
    public static (bool Ok, string? Error, IReadOnlyList<ErrorLogEntry> Entries) ReadErrorLog(PhysicalDriveInfo drive)
    {
        using var h = DeviceIo.Open(drive.DevicePath, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            return (false, err == 5 ? "读取日志需要管理员权限（Win32 5）" : "打开设备失败（Win32 " + err + "）", []);
        }

        if (drive.Protocol == StorageProtocolKind.Nvme && !drive.IsUsb)
        {
            byte[]? log = DeviceIo.QueryProtocolSpecific(h, StoragePropertyId.StorageDeviceProtocolSpecificProperty,
                ProtocolType.Nvme, (int)NvmeDataType.LogPage, 0x01, 4096);
            if (log is null) return (false, "读取 NVMe 错误日志失败", []);
            return (true, null, ParseNvmeErrorLog(log));
        }

        var task = DeviceIoAta.SmartTask(ATA_SMART_READ_LOG, lbaLow: 0x01, lbaMid: 0x00, sectorCount: 1);
        if (!DeviceIoAta.AtaPassThrough(h, new DeviceIoAta.AtaCommand(task, true, 512), null, out var data, out int e2)
            || data is null)
            return (false, "读取 ATA 错误日志失败（Win32 " + e2 + "）", []);

        return (true, null, ParseAtaErrorLog(data));
    }

    /// <summary>
    /// 解析 NVMe Error Information Log（每条约 64 B，最多 63 条）。**纯函数**，便于单测。
    ///
    /// 布局（NVMe 规范 Error Information Log Entry）：
    ///   [0-1] 错误计数　[2-3] SQID　[4-5] CMDID　[6-7] Status Field　[8-15] LBA
    ///
    /// 曾经把 Uncorrectable 写成 "log[o+1] &amp; 1" —— 那是**错误计数的高字节**，
    /// 与错误性质毫无关系。正解是看 Status Field（[6-7]）的 SCT（Status Code Type，bit 9-11）：
    /// SCT=2 表示「介质与数据完整性错误」，这才是可称之为不可恢复的那一类。
    /// </summary>
    public static IReadOnlyList<ErrorLogEntry> ParseNvmeErrorLog(byte[] log)
    {
        var list = new List<ErrorLogEntry>();
        for (int i = 0; i < 63; i++)
        {
            int o = i * 64;
            if (o + 64 > log.Length) break;
            if (log[o] == 0 && log[o + 1] == 0 && log[o + 2] == 0 && log[o + 3] == 0) continue;

            ushort statusField = BitConverter.ToUInt16(log, o + 6);
            int sct = (statusField >> 9) & 0x7;
            int sc = (statusField >> 1) & 0xFF;
            list.Add(new ErrorLogEntry(i + 1,
                BitConverter.ToUInt16(log, o),                 // 错误计数
                (long)BitConverter.ToUInt64(log, o + 8),       // 出错时命令访问的 LBA
                sct == 2,                                       // SCT=2 = 介质与数据完整性错误
                "SCT=" + sct + " SC=0x" + sc.ToString("X2")));
        }
        return list;
    }

    /// <summary>
    /// 解析 ATA Error log（SMART READ LOG，log 地址 01h，每条约 90 B，取前 5 条）。**纯函数**。
    /// 本机没有 SATA 盘，这段只能靠单测兜住 —— 所以它必须可测。
    ///
    /// 布局（ACS-4 Error log 表）：
    /// 日志整体布局：**[0] 日志版本　[1] 条目数　[2 + i*90] 第 i 条**（共 512 B，最多 5 条）。
    ///
    /// 单条 90 B 内的字段（base = 2 + i*90）：
    ///   [+0]   Error 寄存器（bit6 UNC = 不可纠正）
    ///   [+1]   Status 寄存器
    ///   [+2-3] 扩展错误码（小端 16 位）← 这才是真正的「错误码」
    ///   [+4]   扇区数
    ///   [+5-7] LBA 低/中/高（小端 24 位）← 错误日志只记 28 位 LBA 的低 24 位
    ///   [+8]   Device 寄存器
    ///   [+9]   ATA 命令
    ///
    /// ⚠️ 曾经有两处错：① 条目基址用 i*90（把 2 字节日志头当成了第 0 条的开头）；
    /// ② 错误码取 [+0]（那是 Error 寄存器）、LBA 取 [+3..+6] 共 4 字节（多读 1 字节，把 Device 混进高位）。
    /// 结果是报出来的 LBA 是垃圾值 —— 而这条路径本机没有 SATA 盘，只能靠单测兜住。
    /// </summary>
    public static IReadOnlyList<ErrorLogEntry> ParseAtaErrorLog(byte[] data)
    {
        var list = new List<ErrorLogEntry>();
        if (data.Length < 2) return list;
        byte count = data[1];
        for (int i = 0; i < Math.Min(5, (int)count); i++)
        {
            int o = 2 + i * 90;          // ← 跳过 2 字节日志头
            if (o + 90 > data.Length) break;
            long errorCode = (ushort)(data[o + 2] | (data[o + 3] << 8));
            long lba = data[o + 5] | ((long)data[o + 6] << 8) | ((long)data[o + 7] << 16);
            list.Add(new ErrorLogEntry(i + 1, errorCode, lba,
                (data[o] & 0x40) != 0,                            // Error 寄存器 bit6 = UNC
                "Err=0x" + data[o].ToString("X2") + " St=0x" + data[o + 1].ToString("X2")
                + " Cmd=0x" + data[o + 9].ToString("X2") + " 扇区数=" + data[o + 4]));
        }
        return list;
    }
}
