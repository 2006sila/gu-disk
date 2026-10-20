using System.Security.Cryptography;
using System.Text;

namespace GuDisk.Core;

/// <summary>
/// 一块物理磁盘的稳定主键：SHA-256(型号 + 序列号) 取前 16 位十六进制。
/// 设备号（PhysicalDriveN）会随热插拔漂移，禁止用它做持久化键。
/// </summary>
public static class DiskKey
{
    /// <summary>
    /// 序列号是否"看起来是假值"。
    ///
    /// USB 桥接器经常不转发真实的 UNIT SERIAL NUMBER，Windows 拿到的是形如 "000000000203"
    /// 的占位串。两块同型号盘会得到**完全相同**的假序列号，于是 DiskKey 也相同 ——
    /// 历史、告警、悬浮窗勾选全部串在一起（实测：两块 WD20EURS 双盘位盒）。
    /// 判定：纯数字且去掉前导零后不足 4 位 → 假值（真序列号总有字母或足够长）。
    /// </summary>
    public static bool LooksFakeSerial(string? serial)
    {
        string s = (serial ?? "").Trim();
        return s.Length == 0 || (s.All(char.IsDigit) && s.TrimStart('0').Length < 4);
    }

    public static string From(string? model, string? serial)
    {
        var raw = (model ?? "").Trim().ToUpperInvariant() + "|" + (serial ?? "").Trim().ToUpperInvariant();
        if (raw == "|") return "UNKNOWN";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
