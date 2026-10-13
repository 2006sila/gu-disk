using Microsoft.Win32.SafeHandles;
using GuDisk.Core.Decode;
using GuDisk.Core.Interop;
using static GuDisk.Core.Interop.AtaStructures;

namespace GuDisk.Core.Smart;

/// <summary>
/// ATA / SATA 路径：先试 IOCTL_ATA_PASS_THROUGH_DIRECT（直连），失败退 SCSI ATA PASS-THROUGH(16)（USB 盒 / RAID 接管）。
///
/// ⚠️ 本机没有 SATA 盘也未提权，**这两条路径尚未真机验证**（只验证了管线不崩、错误码合理）。
/// 需要接一块 SATA SSD/机械盘，并以管理员运行 <c>Gu-disk.Cli smart &lt;n&gt; --probe</c> 才能确认命令字。
/// </summary>
public static class AtaSmartReader
{
    public sealed record AtaResult(bool Success, string? Error, int Win32Error,
        AtaSmartBlock? Smart, IdentityInfo? Identity, string Transport);

    public static AtaResult Read(SafeFileHandle h, Devices.PhysicalDriveInfo drive)
    {
        // ---- 1) 直连 ATA ----
        var (ok1, err1, data512, thr512, id512, status) = ReadViaAtaDirect(h);
        if (ok1 && status.Ok)
            return Build(data512, thr512, id512, status, "IOCTL_ATA_PASS_THROUGH_DIRECT");

        // ---- 2) SCSI ATA PASS-THROUGH(16) ----
        var (ok2, err2, data2, thr2, id2, status2) = ReadViaScsi16(h);
        if (ok2 && status2.Ok)
            return Build(data2, thr2, id2, status2, "IOCTL_SCSI_PASS_THROUGH_DIRECT + ATA PASS-THROUGH(16)");

        int err = err1 != 0 ? err1 : err2;
        string which = !ok1 && err1 == 5 ? "两种透传都需要管理员权限（Win32 5）"
                    : "ATA 直通失败(Win32 " + err1 + ")，SCSI ATA-16 失败(Win32 " + err2 + ")";
        return new AtaResult(false, "未能读取 ATA SMART：" + which, err, null, null, "");
    }

    private static (bool Ok, int Err, byte[]? Data, byte[]? Thr, byte[]? Id, (bool Ok, bool Passed, string Raw))
        ReadViaAtaDirect(SafeFileHandle h)
    {
        bool ok = true; int err = 0, firstErr = 0;

        bool r1 = DeviceIoAta.AtaPassThrough(h, new DeviceIoAta.AtaCommand(
            DeviceIoAta.SmartTask(ATA_SMART_READ_DATA), true, SMART_DATA_LENGTH), null, out var data, out err);
        if (!r1) { ok = false; firstErr = err; }

        bool r2 = DeviceIoAta.AtaPassThrough(h, new DeviceIoAta.AtaCommand(
            DeviceIoAta.SmartTask(ATA_SMART_READ_THRESHOLD), true, SMART_DATA_LENGTH), null, out var thr, out err);
        if (!r2) { ok = false; if (firstErr == 0) firstErr = err; }

        bool r3 = DeviceIoAta.AtaPassThrough(h, new DeviceIoAta.AtaCommand(
            DeviceIoAta.IdentifyTask(), true, SMART_DATA_LENGTH), null, out var id, out err);
        if (!r3) { ok = false; if (firstErr == 0) firstErr = err; }

        var st = ReadStatusVia(AtaTransport.Direct, h);
        return (ok, firstErr, data, thr, id, st);
    }

    private static (bool Ok, int Err, byte[]? Data, byte[]? Thr, byte[]? Id, (bool Ok, bool Passed, string Raw))
        ReadViaScsi16(SafeFileHandle h)
    {
        bool ok = true; int err = 0, firstErr = 0;

        byte[] SmartCdb(byte sub, byte low, byte mid, ushort count)
            => AtaStructures.BuildAtaPassThrough16(AtaProtocol.PioDataIn, AtaTransfers.SectorCount,
                   sub, count, low, mid, 0, 0xA0, ATA_SMART_CMD);

        bool r1 = DeviceIoAta.ScsiAtaPassThrough16(h, SmartCdb(ATA_SMART_READ_DATA, SMART_LBA_LOW, SMART_LBA_MID, 1),
            SMART_DATA_LENGTH, true, null, out var data, out _, out err);
        if (!r1) { ok = false; firstErr = err; }

        bool r2 = DeviceIoAta.ScsiAtaPassThrough16(h, SmartCdb(ATA_SMART_READ_THRESHOLD, SMART_LBA_LOW, SMART_LBA_MID, 1),
            SMART_DATA_LENGTH, true, null, out var thr, out _, out err);
        if (!r2) { ok = false; if (firstErr == 0) firstErr = err; }

        byte[] IdCdb = AtaStructures.BuildAtaPassThrough16(AtaProtocol.PioDataIn, AtaTransfers.SectorCount,
            0, 1, 0, 0, 0, 0xA0, ATA_IDENTIFY_DEVICE);
        bool r3 = DeviceIoAta.ScsiAtaPassThrough16(h, IdCdb, SMART_DATA_LENGTH, true, null, out var id, out _, out err);
        if (!r3) { ok = false; if (firstErr == 0) firstErr = err; }

        var st = ReadStatusVia(AtaTransport.Scsi16, h);
        return (ok, firstErr, data, thr, id, st);
    }

    private enum AtaTransport { Direct, Scsi16 }

    /// <summary>SMART RETURN STATUS：LBA Mid=0x4F/Low=0xC2 表示通过，0xF4/0x2C 表示失败。</summary>
    private static (bool Ok, bool Passed, string Raw) ReadStatusVia(AtaTransport t, SafeFileHandle h)
    {
        if (t == AtaTransport.Direct)
        {
            bool ok = DeviceIoAta.AtaPassThrough(h, new DeviceIoAta.AtaCommand(
                DeviceIoAta.SmartTask(ATA_SMART_RETURN_STATUS, sectorCount: 0), false, 0), null, out _, out _);
            return (ok, ok, ok ? "PASSED" : "未知");
        }
        else
        {
            byte[] cdb = AtaStructures.BuildAtaPassThrough16(AtaProtocol.NonData, AtaTransfers.NoData,
                ATA_SMART_RETURN_STATUS, 0, SMART_LBA_LOW, SMART_LBA_MID, 0, 0xA0, ATA_SMART_CMD);
            bool ok = DeviceIoAta.ScsiAtaPassThrough16(h, cdb, 0, false, null, out _, out _, out _);
            return (ok, ok, ok ? "PASSED" : "未知");
        }
    }

    /// <summary>解析 512 字节的 SMART READ DATA 块（公开以便离线分析原始转储与单元测试）。</summary>
    public static AtaResult Build(byte[]? data, byte[]? thr, byte[]? id,
        (bool Ok, bool Passed, string Raw) status, string transport)
    {
        if (data is null || data.Length < SMART_DATA_LENGTH)
            return new AtaResult(false, "SMART 数据长度不足", 0, null, null, transport);

        var thresholds = new Dictionary<byte, byte>();
        if (thr is not null && thr.Length >= SMART_DATA_LENGTH)
            for (int i = 0; i < 30; i++)
            {
                int off = 2 + i * 12;
                byte tid = thr[off];
                if (tid == 0) continue;
                thresholds[tid] = thr[off + 1];
            }

        var attrs = new List<SmartAttribute>();
        for (int i = 0; i < 30; i++)
        {
            int off = 2 + i * 12;
            byte idv = data[off];
            if (idv == 0) continue;
            // ATA 属性项 12 字节布局（ACS-3 Table 187）：
            //   [0] ID  [1-2] 状态标志  [3] 当前值  [4] 最差值  [5-10] 原始值(6 字节小端)  [11] 保留
            // ⚠️ 曾经写成 for (b = 5; b >= 0; b--)，把 **[0] 属性 ID 当成了最低字节**、
            //    还丢掉了最高两字节 —— 机械盘上 C5 会显示成一个天文数字。已修。
            int current = data[off + 3], worst = data[off + 4];
            ulong raw = 0;
            for (int b = 10; b >= 5; b--) raw = (raw << 8) | data[off + b];
            int threshold = thresholds.TryGetValue(idv, out var t) ? t : 0;
            string cat = AtaAttributeTable.CategoryOf(idv);
            string st2 = threshold > 0 && current <= threshold ? "危险"
                        : threshold > 0 && current <= threshold + 5 ? "注意"
                        : (raw > 0 && cat == "坏道") ? "注意" : "正常";
            attrs.Add(new SmartAttribute(idv, AtaAttributeTable.NameOf(idv), "", current, worst, threshold,
                raw, raw.ToString("X12"), FormatRaw(idv, raw), st2, cat));
        }

        IdentityInfo? identity = id is not null && id.Length >= 512 ? ParseIdentify(id) : null;
        var block = new AtaSmartBlock(status.Passed, attrs, data);
        return new AtaResult(true, null, 0, block, identity, transport);
    }

    private static string FormatRaw(byte id, ulong raw) => id switch
    {
        0x09 => UnitConverter.FormatCount((long)raw) + " 小时",
        0x0C or 0x04 or 0xC0 or 0xC1 => UnitConverter.FormatCount((long)raw) + " 次",
        // 温度：低字节 = 当前，次低 = 最低，第三字节 = 最高（主流约定；个别厂商顺序不同）
        0xC2 or 0xBE => (raw & 0xFF) + " ℃（当前）/ " + ((raw >> 8) & 0xFF) + " ℃（最低）/ " + ((raw >> 16) & 0xFF) + " ℃（最高）",
        0xF1 or 0xF2 => UnitConverter.FormatBytes(raw * 512d) + "（" + UnitConverter.FormatCount((long)raw) + " 个 LBA）",
        0x05 or 0xC5 or 0xC6 => UnitConverter.FormatCount((long)raw) + " 个扇区",
        0x16 => ((raw >> 16) & 0xFF) == 0 ? "N/A（非氦气盘）" : ((raw >> 16) & 0xFF) + " / 100",
        _ => UnitConverter.FormatCount((long)raw),
    };

    /// <summary>ATA 字符串是"以字为单位字节交换"，必须按高字节在前还原。</summary>
    private static string AtaString(byte[] id, int wordStart, int wordCount)
    {
        var sb = new System.Text.StringBuilder(wordCount * 2);
        for (int w = 0; w < wordCount; w++)
        {
            int off = (wordStart + w) * 2;
            char hi = (char)id[off + 1], lo = (char)id[off];
            sb.Append(hi); sb.Append(lo);
        }
        return sb.ToString().Trim();
    }

    public static IdentityInfo ParseIdentify(byte[] id)
    {
        ushort Word(int n) => (ushort)(id[n * 2] | (id[n * 2 + 1] << 8));

        string serial = AtaString(id, IdentifyWord.Serial, 10);
        string firmware = AtaString(id, IdentifyWord.Firmware, 4);
        string model = AtaString(id, IdentifyWord.Model, 20);

        ulong lba48 = 0;
        for (int w = 3; w >= 0; w--) lba48 = (lba48 << 16) | Word(IdentifyWord.Lba48Sectors + w);
        if (lba48 == 0) lba48 = (ulong)Word(60) | ((ulong)Word(61) << 16);

        // 标称转速（word 217）：0x0001 = 非旋转介质（SSD）；> 0x0400 且 < 0xFFFF 时
        // **转速 = 该值 - 1**（ATA8-ACS 编码，与 smartmontools 一致）。
        // 早期版本直接把 word 值当转速，机械盘会显示成 7201 而不是 7200 —— 已修。
        int rot = Word(IdentifyWord.RotationRate);
        int? rpm = rot switch
        {
            0x0001 => null,                                   // SSD
            > 0x0400 and < 0xFFFF => rot - 1,                 // 5401→5400、7201→7200
            _ => null,
        };

        bool trim = (Word(IdentifyWord.TrimSupported) & 0x0001) != 0;
        ushort zoned = Word(IdentifyWord.ZonedCapability);
        bool zonedDevice = (zoned & 0x4000) != 0 || (zoned & 0x0100) != 0;
        ushort sata = Word(IdentifyWord.SataSpeed);
        string speed = (sata & 0x0008) != 0 ? "SATA 6 Gb/s" : (sata & 0x0004) != 0 ? "SATA 3 Gb/s"
                     : (sata & 0x0002) != 0 ? "SATA 1.5 Gb/s" : "未知";
        int major = Word(IdentifyWord.MajorVersion);

        return new IdentityInfo(
            Model: model, Serial: serial, Firmware: firmware,
            Standard: "ATA-8+ (major 0x" + major.ToString("X4") + ")",
            Interface: "SATA", TransferMode: speed, SupportedMode: speed,
            RotationRateRpm: rpm,
            PowerOnHours: 0, PowerOnCount: 0,
            TrimSupported: trim, ZonedDevice: zonedDevice,
            NcQSupported: (Word(76) & 0x0100) != 0 || (Word(75) & 0x1F) != 0,
            Capacities: UnitConverter.FormatBytes(lba48 * 512d) + "（" + lba48 + " 扇区）",
            FormFactor: (Word(168) & 0x0F) switch
            {
                0 => "5.25 英寸", 1 => "3.5 英寸", 2 => "2.5 英寸", 3 => "1.8 英寸",
                4 => "小于 1.8 英寸", _ => "未报告",
            });
    }
}
