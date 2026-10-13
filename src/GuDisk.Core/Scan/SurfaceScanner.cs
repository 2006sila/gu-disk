using System.Diagnostics;
using GuDisk.Core.Decode;

namespace GuDisk.Core.Scan;

public enum BlockClass { Ok, Slow, Timeout, Error }

/// <summary>坏块表里的一行。</summary>
public sealed record ScanBlock(int Index, long OffsetBytes, long LengthBytes, BlockClass Class,
    double MibPerSecond, int Win32Error, string Explanation);

/// <summary>热力图的桶（默认每 1 MiB 一桶）。</summary>
public sealed record ScanBucket(long OffsetBytes, long LengthBytes, BlockClass Worst, double MibPerSecond);

public sealed record ScanProgress(long DoneBytes, long TotalBytes, double MibPerSecond,
    TimeSpan Elapsed, TimeSpan? Eta, int ProblemCount)
{
    public double Percent => TotalBytes > 0 ? DoneBytes * 100.0 / TotalBytes : 0;
}

public sealed record ScanResult(bool Success, string? Error, string Mode, long TotalBytes, uint BlockSize,
    TimeSpan Elapsed, long ScannedBytes, int ScannedBlocks, double AverageMibPerSecond,
    double MinMibPerSecond, double MaxMibPerSecond,
    IReadOnlyList<ScanBlock> ProblemBlocks, IReadOnlyList<ScanBucket> Map, string Conclusion)
{
    public int ErrorCount => ProblemBlocks.Count(b => b.Class is BlockClass.Error or BlockClass.Timeout);
    public int SlowCount => ProblemBlocks.Count(b => b.Class == BlockClass.Slow);
}

/// <summary>
/// 表面扫描（坏道检测）。
///
/// 两种模式：
///   file —— 在自建临时文件范围内扫描（**默认，安全**，不碰用户数据）
///   raw  —— 直读整盘（**需要管理员**，只读）
///
/// 策略：正常路径按队列深度批量读（快），**一旦某批失败就回退到 QD1 逐块重读定位** ——
/// 批式读会把错误码吞掉，所以定位必须回到单块粒度。
/// </summary>
public static class SurfaceScanner
{
    public const uint DefaultBlockSize = 1024u * 1024;

    /// <summary>Win32 错误码 → 中文解释。</summary>
    public static string Explain(int code) => code switch
    {
        0 => "正常",
        23 => "数据校验错误（CRC）：扇区能读但校验不过 —— 坏道，或 SATA 线缆/接口问题",
        27 => "扇区未找到：该 LBA 已被固件标记为坏",
        1117 => "I/O 设备错误：通常伴随介质超时或重试失败",
        1167 => "设备未就绪",
        21 => "设备已就绪但无法访问（可能正在做内部维护）",
        1 => "功能不受支持（该偏移不属于此设备）",
        5 => "拒绝访问（裸盘扫描需要管理员权限）",
        1460 => "读取超时（超过设定的等待时间）",
        8 => "数据不足",
        _ => "未知错误码"
    };

    /// <param name="timeoutMs">
    /// 单块/单批读的等待上限，超时即判为 <see cref="BlockClass.Timeout"/>（对应界面的"损坏"档）。
    /// 默认 3000 与参考软件一致：机械盘正常块只要 5–12 ms，超过 3 秒不响应基本可以判死；
    /// 调大只会让有坏道的盘扫得更慢，不会让它更准。
    /// </param>
    public static ScanResult RunFile(string path, long size, uint blockSize = DefaultBlockSize,
        int queueDepth = 8, IProgress<ScanProgress>? progress = null, CancellationToken ct = default,
        int timeoutMs = 3000)
    {
        var (file, err) = Benchmark.DirectIoFile.Create(path, size, blockSize);
        if (file is null) return Fail(err);
        try
        {
            string? prep = file.Prepare("random");
            if (prep is not null) return Fail(prep);
            return Scan(file, blockSize, queueDepth, "file（扫描测试文件）", progress, ct, timeoutMs);
        }
        finally { file.Dispose(); }
    }

    public static ScanResult RunRaw(int deviceNumber, uint blockSize = DefaultBlockSize,
        int queueDepth = 8, IProgress<ScanProgress>? progress = null, CancellationToken ct = default,
        int timeoutMs = 3000)
    {
        string dev = new string(System.IO.Path.DirectorySeparatorChar, 2) + "."
                   + System.IO.Path.DirectorySeparatorChar + "PhysicalDrive" + deviceNumber;
        var (bytes, sector, gerr) = Benchmark.RawDevice.Size(dev);
        if (gerr is not null) return Fail(gerr);
        if (bytes <= 0) return Fail("无法取得设备容量");

        uint bs = Math.Max(blockSize, sector);
        var (file, oerr) = Benchmark.DirectIoFile.OpenRaw(dev, bytes, bs);
        if (file is null) return Fail(oerr);
        try { return Scan(file, bs, queueDepth, "raw（只读扫描整盘 " + dev + "）", progress, ct, timeoutMs); }
        finally { file.Dispose(); }
    }

    private static ScanResult Scan(Benchmark.DirectIoFile file, uint bs, int qd, string mode,
        IProgress<ScanProgress>? progress, CancellationToken ct, int timeoutMs)
    {
        long total = file.Length;
        long aligned = total - total % bs;
        var sw = Stopwatch.StartNew();
        var problems = new List<ScanBlock>();
        var buckets = new List<ScanBucket>();
        int index = 0;
        long done = 0;
        double min = double.MaxValue, max = 0, sum = 0;
        int counted = 0;
        long batchBytes = (long)bs * Math.Max(1, qd);

        while (done < aligned)
        {
            ct.ThrowIfCancellationRequested();
            long len = Math.Min(batchBytes, aligned - done);
            var r = file.ReadRange(done, len, bs, Math.Max(1, qd), timeoutMs);

            if (r.Success)
            {
                double speed = r.MibPerSecond;
                sum += speed; counted++;
                min = Math.Min(min, speed);
                max = Math.Max(max, speed);
                for (long off = done; off < done + len; off += bs)
                {
                    buckets.Add(new ScanBucket(off, bs, BlockClass.Ok, speed));
                    index++;
                }
            }
            else
            {
                for (long off = done; off < done + len; off += bs)
                {
                    ct.ThrowIfCancellationRequested();
                    uint thisLen = (uint)Math.Min(bs, aligned - off);
                    var one = file.ReadChunk(off, thisLen, timeoutMs);
                    index++;
                    if (one.Ok)
                    {
                        sum += one.MibPerSecond; counted++;
                        min = Math.Min(min, one.MibPerSecond);
                        max = Math.Max(max, one.MibPerSecond);
                        buckets.Add(new ScanBucket(off, thisLen, BlockClass.Ok, one.MibPerSecond));
                    }
                    else
                    {
                        var cls = one.Win32Error == 1460 ? BlockClass.Timeout : BlockClass.Error;
                        problems.Add(new ScanBlock(index, off, thisLen, cls, 0, one.Win32Error, Explain(one.Win32Error)));
                        buckets.Add(new ScanBucket(off, thisLen, cls, 0));
                    }
                }
            }

            done += len;
            if (progress is not null && index % Math.Max(1, qd) == 0)
            {
                var elapsed = sw.Elapsed;
                double avg = done > 0 ? done / (1024d * 1024d) / Math.Max(1e-9, elapsed.TotalSeconds) : 0;
                TimeSpan? eta = avg > 0.01 ? TimeSpan.FromSeconds((total - done) / (1024d * 1024d) / avg) : null;
                progress.Report(new ScanProgress(done, total, avg, elapsed, eta, problems.Count));
            }
        }

        sw.Stop();
        double avgSpeed = counted > 0 ? sum / counted : 0;

        // 慢块判定：
        //   ① 基线用**中位数**而不是平均值 —— 平均值会被少数极慢块拉低，导致连锁误判；
        //   ② **跳过开头的预热段**（前 5%）—— 队列刚填满时本来就慢，早期版本因此把首批 8 块全报成"偏慢"。
        if (avgSpeed > 0)
        {
            var okSpeeds = buckets.Where(b => b.Worst == BlockClass.Ok)
                                  .Select(b => b.MibPerSecond).OrderBy(v => v).ToList();
            double median = okSpeeds.Count > 0 ? okSpeeds[okSpeeds.Count / 2] : 0;
            long warmupBytes = Math.Max(batchBytes, aligned / 20);

            for (int i = 0; i < buckets.Count; i++)
            {
                var b = buckets[i];
                if (b.Worst != BlockClass.Ok) continue;
                if (b.OffsetBytes < warmupBytes) continue;
                if (median > 0 && b.MibPerSecond < median * 0.3)
                {
                    buckets[i] = b with { Worst = BlockClass.Slow };
                    problems.Add(new ScanBlock(i, b.OffsetBytes, b.LengthBytes, BlockClass.Slow,
                        b.MibPerSecond, 0, "读取速度低于中位数 30%（" + b.MibPerSecond.ToString("N0")
                        + " vs 中位 " + median.ToString("N0") + " MB/s）"));
                }
            }
        }
        problems.Sort((a, b) => a.OffsetBytes.CompareTo(b.OffsetBytes));

        int errors = problems.Count(p => p.Class is BlockClass.Error or BlockClass.Timeout);
        string conclusion = errors > 0
            ? "发现 " + errors + " 个不可读块 —— 属真实坏道，建议立即备份并更换硬盘；"
              + "若经 USB 盒读取且错误码是 CRC(23)，先换线换口复测再下结论。"
            : problems.Count > 0
                ? "未发现不可读块，但有 " + problems.Count + " 个明显偏慢的块，可能是盘片老化或后台整理，建议复测一次。"
                : "未发现坏块，读取速度均匀。";

        return new ScanResult(true, null, mode, total, bs, sw.Elapsed, done, index,
            avgSpeed, min == double.MaxValue ? 0 : min, max, problems, buckets, conclusion);
    }

    private static ScanResult Fail(string? error)
        => new(false, error ?? "未知错误", "", 0, 0, TimeSpan.Zero, 0, 0, 0, 0, 0, [], [], "");

    /// <summary>导出问题块为 CSV。</summary>
    public static string ToCsv(ScanResult r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("offset_bytes,offset_human,length_bytes,class,speed_mibs,win32,explanation");
        foreach (var b in r.ProblemBlocks)
        {
            string esc = b.Explanation.Replace("\"", "\"\"");
            sb.AppendLine(b.OffsetBytes + "," + UnitConverter.FormatBytes(b.OffsetBytes) + ","
                + b.LengthBytes + "," + b.Class + "," + b.MibPerSecond.ToString("0.0") + ","
                + b.Win32Error + ",\"" + esc + "\"");
        }
        return sb.ToString();
    }

    public static string Summary(ScanResult r)
        => r.Success
            ? UnitConverter.FormatBytes(r.ScannedBytes) + " · " + r.ScannedBlocks + " 块 · 平均 "
              + r.AverageMibPerSecond.ToString("N0") + " MB/s · 耗时 " + r.Elapsed.ToString()
              + " · 坏块 " + r.ErrorCount + " · 偏慢 " + r.SlowCount
            : "失败：" + r.Error;
}
