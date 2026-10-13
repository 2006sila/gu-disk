using GuDisk.Core.Decode;
using GuDisk.Core.Smart;

namespace GuDisk.Core.Health;

public enum MediaTechnology { Unknown, Ssd, HddCmr, HddSmr, HddSmrZoned }

public sealed record MediaVerdict(
    MediaTechnology Technology,
    string Label,
    int ConfidencePercent,
    string Reason,
    bool TrimSupported);

/// <summary>
/// 介质类型判定：SSD / 机械(CMR) / 机械(SMR)。
/// 证据链按强度排序：drivedb 官方 "(SMR)" 标注 → ATA ZAC/ZBC → drivedb 型号族 → TRIM 旁证。
/// **永远带依据与置信度**，判不出来就说判不出来。
/// </summary>
public static class CmrSmrDetector
{
    public static MediaVerdict Detect(
        string model,
        IdentityInfo? identity,
        NvmeSmartLog? nvme,
        AtaSmartBlock? ata,
        DrivedbIndex? drivedb)
    {
        bool isNvme = nvme is not null;
        bool trim = identity?.TrimSupported ?? false;
        int? rpm = identity?.RotationRateRpm;

        // ---- 1) SSD 判定 ----
        if (isNvme)
            return new MediaVerdict(MediaTechnology.Ssd, "SSD（NVMe）", 100,
                "NVMe 协议设备，必为固态", trim);

        bool ssdByRotation = rpm is null && identity is not null && !identity.ZonedDevice;
        bool hasEraseAttr = ata?.Attributes.Any(a => a.Id is 0xAD or 0xAB or 0xA9 or 0xE7 or 0xE8) ?? false;
        if (hasEraseAttr || (ssdByRotation && ata?.Attributes.Any(a => a.Id == 0xF1 || a.Id == 0xF2) == true))
            return new MediaVerdict(MediaTechnology.Ssd, "SSD（SATA）", 95,
                hasEraseAttr ? "存在擦除计数/剩余寿命类属性，且无转速" : "无转速且带主机读写量属性", trim);

        // ---- 2) 叠瓦（SMR）判定 ----
        if (identity?.ZonedDevice == true)
            return new MediaVerdict(MediaTechnology.HddSmrZoned, "SMR 叠瓦盘（主机管理型 / ZAC-ZBC）", 100,
                "硬盘固件声明支持 ZAC/ZBC 分区命令集", trim);

        var smr = drivedb?.Match(model, smrOnly: true);
        if (smr is not null)
            return new MediaVerdict(MediaTechnology.HddSmr, "SMR 叠瓦盘", 100,
                "命中 smartmontools drivedb 官方 SMR 标注：" + smr.Family + (trim ? "，且底层声明支持 TRIM" : ""), trim);

        var cmr = drivedb?.Match(model, cmrOnly: true);
        if (cmr is not null)
            return new MediaVerdict(MediaTechnology.HddCmr, "CMR 垂直盘", 100,
                "命中 smartmontools drivedb 官方 CMR 标注：" + cmr.Family, trim);

        // 兜底：旧版内置型号正则（drivedb 没收录的新盘/冷门盘）
        if (LooksLikeSmr(model))
            return new MediaVerdict(MediaTechnology.HddSmr, "SMR 叠瓦盘（疑似）", 85,
                "命中内置叠瓦型号特征" + (trim ? "；且声明支持 TRIM（消费级 SMR 的典型旁证）" : ""), trim);
        if (LooksLikeCmr(model))
            return new MediaVerdict(MediaTechnology.HddCmr, "CMR 垂直盘", 90, "命中内置垂直型号特征", trim);

        if (rpm is not null)
            return new MediaVerdict(MediaTechnology.Unknown, "机械盘（介质类型未收录）", 0,
                "转速 " + rpm + " rpm，属机械盘；但 drivedb 与内置特征库都未收录该型号，无法判定 CMR/SMR。"
                + "可用全盘顺序读写曲线观察是否出现 SMR 掉速阶梯。", trim);

        return new MediaVerdict(MediaTechnology.Unknown, "未收录，无法判定", 0,
            "缺少足够证据（无 drivedb 标注、无 ZAC/ZBC、无转速信息）", trim);
    }

    private static bool LooksLikeSmr(string model)
    {
        string m = model.ToUpperInvariant();
        string[] keys =
        [
            "ST2000DM008", "ST2000DM005", "ST3000DM007", "ST4000DM004", "ST6000DM003", "ST8000DM004",
            "ST1000LM048", "ST2000LM015", "ST3000LM024", "ST4000LM024", "ST5000LM000", "ST500LM030",
            "ST8000AS0002", "ST5000AS0011",
            "MQ04", "HDWL1", "HDWT7", "HDWD24", "WD20EZAZ", "WD30EZAZ", "WD40EZAZ", "WD60EZAZ",
            "WD10SPZX", "WD20SPZX", "WD10SPSX",
        ];
        foreach (var k in keys) if (m.Contains(k, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool LooksLikeCmr(string model)
    {
        string m = model.ToUpperInvariant();
        string[] keys =
        [
            "ST1000DM010", "ST2000DM009", "ST3000DM008", "ST4000DM005",
            "ST1000VX", "ST2000VX", "ST4000VX", "ST6000VX", "ST8000VX", "ST10000VX", "ST12000VX",
            "ST1000VN", "ST2000VN", "ST4000VN", "ST6000VN", "ST8000VN", "ST10000VN", "ST12000VN",
            "ST1000NE", "ST2000NE", "ST4000NE", "ST6000NE", "ST8000NE", "ST10000NE", "ST12000NE",
            "ST1000NM", "ST2000NM", "ST4000NM", "ST6000NM", "ST8000NM", "ST10000NM", "ST12000NM", "ST16000NM",
            "WD10EZEX", "WD20EZBX", "WD40EZRX", "WD80EAZZ", "WD100EFAX", "WD120EFAX", "WD140EDGZ",
        ];
        foreach (var k in keys) if (m.Contains(k, StringComparison.Ordinal)) return true;
        return false;
    }
}
