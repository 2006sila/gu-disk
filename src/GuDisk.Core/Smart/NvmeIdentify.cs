using System.Text;

namespace GuDisk.Core.Smart;

/// <summary>
/// NVMe Identify Controller 数据（4096 B）的关键字段。
/// 为什么必须用它：Windows 在 STORAGE_DEVICE_DESCRIPTOR 里给 NVMe 的序列号是"ASCII 的十六进制分组"形式
/// （实测形如 3530_3330_3138_3330_50C6_8E09_5053_4248.），不能直接当序列号用。
/// </summary>
public sealed record NvmeIdentifyController(
    ushort VendorId,
    ushort SubsystemVendorId,
    string SerialNumber,
    string ModelNumber,
    string FirmwareRevision,
    /// <summary>ONCS（Optional NVM Command Support，Identify Controller 0x100）。</summary>
    ushort Oncs,
    byte[] Raw)
{
    /// <summary>
    /// 是否支持 Dataset Management。
    ///
    /// 这就是 **NVMe 版的 TRIM** —— ATA 的 DATA SET MANAGEMENT（DSM）在 NVMe 里叫 Deallocate，
    /// 挂在 ONCS 的 bit 2。以前界面对 NVMe 直接写死"支持 TRIM"，是编的；
    /// 现在读真实能力位。
    /// </summary>
    public bool SupportsDatasetManagement => (Oncs & 0x0004) != 0;
    private static string Ascii(byte[] b, int offset, int length)
    {
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            byte c = b[offset + i];
            if (c == 0) break;
            if (c >= 0x20 && c < 0x7F) sb.Append((char)c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>解析 4096 字节的 Identify Controller 数据（NVMe 规范 1.4+ 图 247）。</summary>
    public static NvmeIdentifyController? Parse(byte[] data)
    {
        if (data.Length < 4096) return null;
        return new NvmeIdentifyController(
            VendorId: BitConverter.ToUInt16(data, 0x00),
            SubsystemVendorId: BitConverter.ToUInt16(data, 0x02),
            SerialNumber: Ascii(data, 0x04, 20),
            ModelNumber: Ascii(data, 0x18, 40),   // MN 在 24..63；写 0x14 会多吃 4 字节序列号尾巴
            FirmwareRevision: Ascii(data, 0x40, 8),
            Oncs: BitConverter.ToUInt16(data, 0x100),
            Raw: data);
    }
}
