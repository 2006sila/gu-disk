using GuDisk.Core.Decode;

namespace GuDisk.Core.Benchmark;

/// <summary>曲线上的一个采样点。</summary>
public sealed record CurvePoint(int Index, double PositionPercent, long OffsetBytes, double MibPerSecond);

public sealed record CurveResult(bool Success, string? Error, string Mode, long TotalBytes, uint BlockSize,
    double AverageMibPerSecond, double MinMibPerSecond, double MaxMibPerSecond,
    IReadOnlyList<CurvePoint> Points, string Diagnosis);

/// <summary>
/// 全盘 / 大文件顺序读曲线。用途：看 SSD 的掉速段（SLC 缓存耗尽）、QLC 的缓外速度、
/// 机械盘的内外圈差异、以及 **SMR 叠瓦盘的阶梯式掉速**（这是判定 SMR 的实测证据）。
///
/// 两种模式：
///   file —— 顺序读一个大文件（不需要管理员）
///   raw  —— 直读 \\.\PhysicalDriveN 整盘（需要管理员；机械盘要用这种才能覆盖全盘）
/// </summary>
public static class FullDiskCurve
{
    public static CurveResult RunFile(string path, long size, uint blockSize = 1024u * 1024,
        int samples = 100, Action<int>? progress = null)
    {
        var (file, err) = DirectIoFile.Create(path, size, blockSize);
        if (file is null) return Fail(err);

        try
        {
            string? prep = file.Prepare("random");
            if (prep is not null) return Fail(prep);
            return Sample(file, blockSize, samples, "file（读测试文件）", progress);
        }
        finally { file.Dispose(); }
    }

    public static CurveResult RunRaw(int deviceNumber, uint blockSize = 1024u * 1024,
        int samples = 100, Action<int>? progress = null)
    {
        string dev = new string(System.IO.Path.DirectorySeparatorChar, 2) + "." + System.IO.Path.DirectorySeparatorChar + "PhysicalDrive" + deviceNumber;
        var (bytes, sector, err) = RawDevice.Size(dev);
        if (err is not null) return Fail(err);
        if (bytes <= 0) return Fail("无法取得设备容量");

        // 直接读裸设备：GENERIC_READ + 无缓存 + 重叠（不写，不需要管理员也能开？实测需要）
        var (file, oerr) = DirectIoFile.OpenRaw(dev, bytes, blockSize);
        if (file is null) return Fail(oerr);
        try { return Sample(file, blockSize, samples, "raw（直读整盘 " + dev + "）", progress); }
        finally { file.Dispose(); }
    }

    private static CurveResult Sample(DirectIoFile file, uint bs, int samples, string mode, Action<int>? progress)
    {
        long total = file.Length;
        long chunk = Math.Max(bs, total / Math.Max(1, samples));
        chunk -= chunk % bs;
        int count = (int)Math.Max(1, total / chunk);
        var points = new List<CurvePoint>();

        for (int i = 0; i < count; i++)
        {
            long offset = i * chunk;
            long len = Math.Min(chunk, total - offset);
            len -= len % bs;
            if (len <= 0) break;

            var r = file.ReadRange(offset, len, bs, queueDepth: 8);
            if (!r.Success) return Fail(r.Error);
            points.Add(new CurvePoint(i, total == 0 ? 0 : offset * 100.0 / total, offset, r.MibPerSecond));
            progress?.Invoke(i + 1);
        }

        if (points.Count == 0) return Fail("没有采到有效数据点");

        double avg = points.Average(p => p.MibPerSecond);
        double min = points.Min(p => p.MibPerSecond);
        double max = points.Max(p => p.MibPerSecond);

        // 阶梯检测：把曲线按位置分成前后两半比较，后段明显低且中间有台阶 → 疑似 SMR / 缓存耗尽
        var first = points.Take(Math.Max(1, points.Count / 4)).Average(p => p.MibPerSecond);
        var last = points.Skip(Math.Max(1, points.Count * 3 / 4)).Average(p => p.MibPerSecond);
        double drop = first > 0 ? (1 - last / first) * 100 : 0;

        string diagnosis;
        if (drop >= 40)
            diagnosis = "后段比前段下降 " + drop.ToString("0") + "%，呈明显阶梯 —— 符合叠瓦(SMR)磁道重组、"
                      + "或 SSD 的 SLC 缓存耗尽后掉到缓外速度；结合介质判定结论一起看。";
        else if (drop >= 15)
            diagnosis = "后段比前段下降 " + drop.ToString("0") + "%，有缓降，属正常范围（外圈/内圈差异或缓存收窄）。";
        else
            diagnosis = "前后段速度基本一致（下降 " + drop.ToString("0") + "%），未见掉速段。";

        return new CurveResult(true, null, mode, total, bs, avg, min, max, points, diagnosis);
    }

    private static CurveResult Fail(string? error)
        => new(false, error ?? "未知错误", "", 0, 0, 0, 0, 0, [], "");

    public static string Summary(CurveResult r)
        => r.Success
            ? "平均 " + r.AverageMibPerSecond.ToString("N0") + " MB/s · 最低 " + r.MinMibPerSecond.ToString("N0")
              + " · 最高 " + r.MaxMibPerSecond.ToString("N0") + " ｜ " + UnitConverter.FormatBytes(r.TotalBytes)
            : "失败：" + r.Error;
}

/// <summary>裸设备信息查询（查询类 IOCTL，不需要管理员）。</summary>
internal static class RawDevice
{
    public static (long Bytes, uint Sector, string? Error) Size(string devicePath)
    {
        using var h = Interop.DeviceIo.Open(devicePath, 0);
        if (h.IsInvalid)
            return (0, 0, "打开设备失败（Win32 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "）");
        var g = Interop.DeviceIo.GetGeometry(h);
        if (g is null) return (0, 0, "读取设备几何失败");
        return ((long)g.Value.Size, g.Value.BytesPerSector, null);
    }
}
