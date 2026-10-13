using System.Globalization;

namespace GuDisk.Core.Decode;

/// <summary>
/// 单位换算与显示格式化。
///
/// 进制选择：**1024 进制**，与 Windows / CrystalDiskInfo 的口径一致 ——
/// 实测对标：Predator GM7 的 DataUnitsRead=25 225 120 → ×512 000 B = 12.915 TB；
/// 除以 1024³ 得 12028，与 CDI 的 HostReads="12028" 逐字一致。用 1000 进制会得到 12915，对不上。
/// （"逐字段与 CDI 一致"这条验收判据要求如此。）
/// </summary>
public static class UnitConverter
{
    /// <summary>NVMe 健康日志的 1 个 data unit = 1000 × 512 B = 512 000 B（CDI 口径，非 1024）。</summary>
    public const double NvmeDataUnitBytes = 512_000d;

    private const double K = 1024d;
    private static readonly CultureInfo Zh = CultureInfo.GetCultureInfo("zh-CN");

    /// <summary>NVMe Health Log 的"读取/写入单位计数" → 字节。</summary>
    public static double NvmeDataUnitsToBytes(ulong units) => units * NvmeDataUnitBytes;

    /// <summary>ATA F1/F2 的 LBA 计数（512B 扇区）→ 字节。</summary>
    public static double LbaToBytes(ulong lba, uint bytesPerSector = 512) => lba * (double)bytesPerSector;

    /// <summary>字节 → "12.0 TB"（1024 进制）；字节档不带小数。千分位用空格，与界面规范一致。</summary>
    public static string FormatBytes(double bytes, int decimals = 1)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = bytes; int i = 0;
        while (v >= K && i < units.Length - 1) { v /= K; i++; }
        int d = i == 0 ? 0 : decimals;
        return v.ToString("N" + d, Zh).Replace(",", " ") + " " + units[i];
    }

    /// <summary>与 CDI 的 "xxxx GB" 口径对齐：固定以 GB(1024³) 表示。</summary>
    public static string FormatGigabytes(double bytes, int decimals = 0)
        => (bytes / (K * K * K)).ToString("N" + decimals, Zh).Replace(",", " ") + " GB";

    /// <summary>计数用空格千分位：23507343456 → "23 507 343 456"。</summary>
    public static string FormatCount(long value) => value.ToString("N0", Zh).Replace(",", " ");

    /// <summary>精确小时（信息网格用）：207 → "207 小时"。</summary>
    public static string FormatHours(long hours) => FormatCount(hours) + " 小时";

    /// <summary>人类可读（风险条 / 报告用）：207 → "8 天 15 小时"；9000 → "1 年 9 天 4 小时"。</summary>
    public static string FormatPowerOnTime(long hours)
    {
        if (hours < 48) return FormatCount(hours) + " 小时";
        long days = hours / 24, h = hours % 24;
        if (days < 365) return days + " 天 " + h + " 小时";
        return (days / 365) + " 年 " + (days % 365) + " 天 " + h + " 小时";
    }

    /// <summary>温度格式化：默认摄氏，可切华氏（界面偏好）。</summary>
    public static string FormatTemp(double celsius, bool fahrenheit = false, string digits = "0.#")
        => fahrenheit
            ? (celsius * 9 / 5 + 32).ToString(digits) + " ℉"
            : celsius.ToString(digits) + " ℃";
}
