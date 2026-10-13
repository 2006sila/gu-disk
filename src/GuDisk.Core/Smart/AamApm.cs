using Microsoft.Win32.SafeHandles;
using GuDisk.Core.Devices;
using GuDisk.Core.Interop;
using static GuDisk.Core.Interop.AtaStructures;

namespace GuDisk.Core.Smart;

/// <summary>
/// AAM（自动噪音管理）/ APM（高级电源管理）控制。**只对机械盘有意义**，SSD 基本不支持。
///
/// ⚠️ 需要管理员权限，且本机没有 SATA 机械盘 —— 命令按规范实现（SET FEATURES 0xEF：
/// AAM 用 Features=0x42 + SectorCount=值；APM 用 Features=0x05 + SectorCount=档位），
/// 真机验证待接硬件。读值走 IDENTIFY DEVICE 的 word 94（AAM）与 word 91（APM）。
/// </summary>
public static class AamApm
{
    public const byte AAM_MIN = 0x80;   // 128 最安静
    public const byte AAM_MAX = 0xFE;   // 254 最快
    public const byte APM_MIN = 0x01;   // 1 最省电
    public const byte APM_MAX = 0xFE;   // 254 最高性能

    public sealed record Values(int? Aam, int? AamRecommended, int? Apm, int? ApmRecommended)
    {
        public bool AamSupported => Aam is > 0;
        public bool ApmSupported => Apm is > 0;
    }

    /// <summary>从 IDENTIFY DEVICE 读 AAM/APM 当前值与厂商推荐值。</summary>
    public static (bool Ok, string? Error, Values? Values) Read(PhysicalDriveInfo drive)
    {
        using var h = DeviceIo.Open(drive.DevicePath, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE);
        if (h.IsInvalid)
        {
            int e = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return (false, e == 5 ? "需要管理员权限（Win32 5）" : "打开设备失败（Win32 " + e + "）", null);
        }
        if (drive.Protocol == StorageProtocolKind.Nvme)
            return (false, "NVMe 盘不支持 AAM/APM（这是机械盘特性）", null);
        if (!DeviceIoAta.AtaPassThrough(h, new DeviceIoAta.AtaCommand(DeviceIoAta.IdentifyTask(), true, 512),
                null, out var id, out int err) || id is null)
            return (false, "IDENTIFY DEVICE 失败（Win32 " + err + "）", null);

        ushort Word(int n) => (ushort)(id[n * 2] | (id[n * 2 + 1] << 8));
        ushort aam = Word(94), apm = Word(91);
        return (true, null, new Values(
            Aam: aam == 0 ? null : aam & 0xFF, AamRecommended: aam == 0 ? null : (aam >> 8) & 0xFF,
            Apm: apm == 0 ? null : apm & 0xFF, ApmRecommended: apm == 0 ? null : (apm >> 8) & 0xFF));
    }

    public static (bool Ok, string? Error, int Win32) SetAam(PhysicalDriveInfo drive, byte value)
    {
        if (value is not 0 and (< AAM_MIN or > AAM_MAX)) return (false, "AAM 取值应在 0x80–0xFE（0 表示关闭）", 0);
        return SetFeature(drive, 0x42, value);
    }

    public static (bool Ok, string? Error, int Win32) SetApm(PhysicalDriveInfo drive, byte level)
    {
        // APM_MIN = 0x01，byte 不可能小于它（0 已被放行表示「关闭」）——
        // 原来写的 "is not 0 and (< APM_MIN or > APM_MAX)" 里有半个死条件，编译器 CS9336 直接点出来了。
        if (level > APM_MAX) return (false, "APM 取值应在 0x00–0xFE（0 表示关闭）", 0);
        return SetFeature(drive, 0x05, level);
    }

    private static (bool Ok, string? Error, int Win32) SetFeature(PhysicalDriveInfo drive, byte feature, byte value)
    {
        using var h = DeviceIo.Open(drive.DevicePath, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE);
        if (h.IsInvalid)
        {
            int e = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return (false, e == 5 ? "需要管理员权限（Win32 5）" : "打开设备失败（Win32 " + e + "）", e);
        }
        var task = new IDEREGS
        {
            Features = feature, SectorCount = value,
            LbaLow = 0, LbaMid = 0, LbaHigh = 0,
            DriveHead = 0xA0, Command = 0xEF, Reserved = 0,
        };
        bool ok = DeviceIoAta.AtaPassThrough(h, new DeviceIoAta.AtaCommand(task, false, 0), null, out _, out int err);
        return ok ? (true, null, 0)
                  : (false, "SET FEATURES 失败（Win32 " + err + "）—— 该盘可能不支持此特性", err);
    }
}
