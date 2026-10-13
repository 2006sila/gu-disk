using System.Runtime.InteropServices;
using GuDisk.Core.Devices;
using GuDisk.Core.Interop;
using static GuDisk.Core.Interop.NativeMethods;
using static GuDisk.Core.Interop.StorageStructures;

namespace GuDisk.Core.Smart;

public sealed record SmartProbeResult(
    bool Success,
    string? Error,
    int Win32Error,
    string Source,
    NvmeSmartLog? Nvme,
    AtaSmartBlock? Ata,
    IReadOnlyList<SmartAttribute> Attributes,
    string Transport,
    IdentityInfo? Identity = null);

/// <summary>
/// 原生引擎（层 1）的公开入口。协议梯：
///   NVMe 直连 → IOCTL_STORAGE_QUERY_PROPERTY（查询类，**不需要管理员**）
///   直连 ATA  → IOCTL_ATA_PASS_THROUGH_DIRECT（需要读写权限）
///   USB/RAID  → IOCTL_SCSI_PASS_THROUGH_DIRECT + ATA PASS-THROUGH(16)（需要读写权限）
/// 可用 <paramref name="forceProtocol"/> 强制走某条路径做诊断（CLI --protocol）。
/// </summary>
public static class SmartProbe
{
    public static SmartProbeResult Read(PhysicalDriveInfo drive, string forceProtocol = "auto")
    {
        bool wantNvme = forceProtocol is "auto" or "nvme";
        bool wantAta = forceProtocol is "auto" or "ata" or "sat";
        bool wantScsi = forceProtocol is "auto" or "scsi" or "sat";
        if (!wantNvme && !wantAta && !wantScsi) { wantNvme = wantAta = wantScsi = true; }

        // ---- 1) NVMe 直连：免管理员 ----
        if (wantNvme && drive.Protocol == StorageProtocolKind.Nvme && !drive.IsUsb)
        {
            using var h0 = DeviceIo.Open(drive.DevicePath, 0);
            if (!h0.IsInvalid) return ReadNvme(h0, drive);
        }

        // ---- 2) ATA / SCSI 透传：必须读写权限 ----
        if (!(wantAta || wantScsi))
            return new SmartProbeResult(false, "该协议未启用", 0, "", null, null, [], "");

        using var h = DeviceIo.Open(drive.DevicePath, GENERIC_READ | GENERIC_WRITE);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            string msg = err switch
            {
                5 => "透传需要管理员权限（ERROR_ACCESS_DENIED）—— 请以管理员身份运行",
                32 => "设备被其它进程独占（ERROR_SHARING_VIOLATION）",
                2 => "设备不存在或已被移除",
                _ => "打开设备失败（Win32 " + err + "）"
            };
            // NVMe 盘即使提权失败，也可以退回查询类路径再试一次
            if (wantNvme && drive.Protocol == StorageProtocolKind.Nvme)
            {
                using var hq = DeviceIo.Open(drive.DevicePath, 0);
                if (!hq.IsInvalid) return ReadNvme(hq, drive);
            }
            return new SmartProbeResult(false, msg, err, "", null, null, [], "");
        }

        if (wantNvme && drive.Protocol == StorageProtocolKind.Nvme)
            return ReadNvme(h, drive);

        if (wantAta || wantScsi)
        {
            var ata = AtaSmartReader.Read(h, drive);
            if (ata.Success && ata.Smart is not null)
                return new SmartProbeResult(true, null, 0, ata.Transport, null, ata.Smart,
                    ata.Smart.Attributes, ata.Transport, ata.Identity);
            return new SmartProbeResult(false, ata.Error, ata.Win32Error, ata.Transport, null, ata.Smart,
                [], ata.Transport, ata.Identity);
        }

        return new SmartProbeResult(false, "没有可用的协议路径", 0, "", null, null, [], "");
    }

    private static SmartProbeResult ReadNvme(Microsoft.Win32.SafeHandles.SafeFileHandle h, PhysicalDriveInfo drive)
    {
        const string src = "NVMe SMART / Health Information Log（IOCTL_STORAGE_QUERY_PROPERTY + StorageDeviceProtocolSpecificProperty）";
        byte[]? log = DeviceIo.QueryProtocolSpecific(h, StoragePropertyId.StorageDeviceProtocolSpecificProperty,
            ProtocolType.Nvme, (int)NvmeDataType.LogPage, 0x02, NvmeSmartReader.LogSize);
        if (log is null)
            return new SmartProbeResult(false,
                "读取 NVMe 健康日志失败（可能需要管理员权限）",
                Marshal.GetLastWin32Error(), src, null, null, [], src);

        var nvme = NvmeSmartReader.Parse(log);
        if (nvme is null)
            return new SmartProbeResult(false, "健康日志长度不足 512 B", 0, src, null, null, [], src);

        return new SmartProbeResult(true, null, 0, src, nvme, null, NvmeSmartReader.ToAttributes(nvme), src);
    }

    /// <summary>把健康日志折成一个可读的概要（CLI 与 UI 共用）。</summary>
    public static IReadOnlyList<(string Key, string Value)> Summarize(NvmeSmartLog n)
    {
        var sensors = n.TemperatureSensors.Count > 1
            ? "（各传感器 " + string.Join(", ", n.TemperatureSensors.Select(s => (s - 273) + "℃")) + "）"
            : "";
        var flags = n.CriticalWarningFlags();
        return
        [
            ("综合温度", (n.CompositeTemperatureC?.ToString("0.#") ?? "-") + " ℃" + sensors),
            ("已用寿命", n.PercentageUsed + " %   · 可用备用空间 " + n.AvailableSpare + " %（阈值 " + n.AvailableSpareThreshold + " %）"),
            ("主机读取", Decode.UnitConverter.FormatBytes(Decode.UnitConverter.NvmeDataUnitsToBytes(n.DataUnitsRead))),
            ("主机写入", Decode.UnitConverter.FormatBytes(Decode.UnitConverter.NvmeDataUnitsToBytes(n.DataUnitsWritten))),
            ("通电", Decode.UnitConverter.FormatCount((long)n.PowerOnHours) + " 小时 · "
                + Decode.UnitConverter.FormatCount((long)n.PowerCycles) + " 次启停 · "
                + Decode.UnitConverter.FormatCount((long)n.UnsafeShutdowns) + " 次不安全关机"),
            ("介质错误", Decode.UnitConverter.FormatCount((long)n.MediaErrors)
                + " · 错误日志 " + Decode.UnitConverter.FormatCount((long)n.ErrorLogEntries) + " 条"),
            ("严重警告", flags.Count == 0 ? "无" : string.Join(" / ", flags)),
        ];
    }
}
