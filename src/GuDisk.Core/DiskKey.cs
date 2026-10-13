using System.Security.Cryptography;
using System.Text;

namespace GuDisk.Core;

/// <summary>
/// 一块物理磁盘的稳定主键：SHA-256(型号 + 序列号) 取前 16 位十六进制。
/// 设备号（PhysicalDriveN）会随热插拔漂移，禁止用它做持久化键。
/// </summary>
public static class DiskKey
{
    public static string From(string? model, string? serial)
    {
        var raw = (model ?? "").Trim().ToUpperInvariant() + "|" + (serial ?? "").Trim().ToUpperInvariant();
        if (raw == "|") return "UNKNOWN";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
