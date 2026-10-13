using System.Diagnostics;

namespace GuDisk.Core.Benchmark;

/// <summary>一行测速（对应 CDM 的一行）。</summary>
public sealed record BenchRow(string Label, uint BlockSize, int QueueDepth, int Threads, bool Random,
    int WritePercent = -1);   // 0-100：混合读写的写比例；-1 = 跟随档位设置，档位也没设就是纯读

/// <summary>实时采样点（喂给实时波形图）。</summary>
public sealed record LiveSample(double Seconds, long TotalBytes, double MibPerSecond);

public sealed record BenchRowResult(string Label, string Mode, bool Success, string? Error,
    double MibPerSecond, double Iops, double LatencyMs, long Bytes, double Seconds);

public sealed record BenchProfile(string Name, IReadOnlyList<BenchRow> Rows, int WritePercent = -1);

public sealed record BenchResult(bool Success, string? Error, string Target, long FileSize,
    string Pattern, int Runs, IReadOnlyList<BenchRowResult> Results, double TotalSeconds);

public static class BenchProfiles
{
    /// <summary>CrystalDiskMark 默认四档（1 MiB 顺 8 队列 / 1 MiB 顺单队列 / 4 KiB 随机 32 队列 16 线程 / 4 KiB 随机单队列）。</summary>
    public static BenchProfile Cdm4 => new("CDM 四档",
    [
        new BenchRow("SEQ1M Q8T1", 1024u * 1024, 8, 1, false),
        new BenchRow("SEQ1M Q1T1", 1024u * 1024, 1, 1, false),
        new BenchRow("RND4K Q32T16", 4096u, 32, 16, true),
        new BenchRow("RND4K Q1T1", 4096u, 1, 1, true),
    ]);

    private static readonly uint[] Ladder =
        [4096u, 8192, 16384, 32768, 65536, 131072, 262144, 524288,
         1048576, 2097152, 4194304, 8388608, 16777216];

    public static string SizeLabel(uint b) => b >= 1048576 ? (b / 1048576) + "M"
        : b >= 1024 ? (b / 1024) + "K" : b + "B";

    /// <summary>ATTO 式阶梯：4K → 16M 共 13 档，队列 4。</summary>
    public static BenchProfile Atto => new("ATTO 阶梯",
        Ladder.Select(b => new BenchRow(SizeLabel(b) + " Q4", b, 4, 1, false)).ToList());

    /// <summary>随机访问测时：512B → 1M 共 8 档，队列 1（比的是单次访问延迟）。</summary>
    public static BenchProfile AccessTime => new("随机访问",
        new uint[] { 512u, 4096, 8192, 16384, 32768, 65536, 1048576 }
            .Select(b => new BenchRow(SizeLabel(b) + " Q1", b, 1, 1, true)).ToList());

    /// <summary>自定义单档：块大小 / 队列 / 线程 / 顺序或随机 / 混合比例全由调用方给。</summary>
    public static BenchProfile Custom(uint blockSize, int queueDepth, int threads, bool random, int writePercent = -1)
        => new("自定义单档", [new BenchRow(SizeLabel(blockSize) + " Q" + queueDepth + "T" + threads,
            blockSize, queueDepth, threads, random, writePercent)]);

    /// <summary>快速档：只跑顺序与随机各一档，用于回归验证。</summary>
    public static BenchProfile Quick => new("快速",
    [
        new BenchRow("SEQ1M Q8T1", 1024u * 1024, 8, 1, false),
        new BenchRow("RND4K Q32T16", 4096u, 32, 16, true),
    ]);
}

/// <summary>基准测速的执行器。自己建测试文件、自己清理；写测试默认关闭（要显式 --write 才开）。</summary>
public static class BenchRunner
{
    public static BenchResult Run(string targetPath, long fileSize, BenchProfile profile,
        string pattern = "random", int runs = 1, bool includeWrite = false, bool keepFile = false,
        Action<string>? log = null, CancellationToken ct = default,
        int durationSeconds = 0, int sampleIntervalMs = 0, Action<LiveSample>? onSample = null)
    {
        var sw = Stopwatch.StartNew();
        fileSize = DirectIoFile.RoundUp(fileSize, 1024 * 1024);

        // 传进来的可能是"目录"也可能是"文件路径"，判定必须覆盖三种情形：
        //   ① 盘符根（E:）  ② 结尾带分隔符（D:\x\）  ③ 已存在的目录（D:\x\bench）
        // 原来只判 ①② —— 传普通目录时会被当成文件名，CreateFile 往一个同名**目录**上创建
        // 必然 ERROR_ACCESS_DENIED(5)，报错信息还只显示目录名，看起来像"目录不可写"。
        string path = targetPath;
        bool isDirectory = path.EndsWith(':') || path.EndsWith("\\") || path.EndsWith("/")
                           || Directory.Exists(path);
        if (isDirectory)
            path = Path.Combine(path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                "Gu-disk_bench_" + Guid.NewGuid().ToString("N")[..8] + ".tmp");

        var (file, err) = DirectIoFile.Create(path, fileSize, 4096);
        if (file is null) return new BenchResult(false, err, path, fileSize, pattern, runs, [], sw.Elapsed.TotalSeconds);

        var results = new List<BenchRowResult>();

        // 实时采样：worker 用 onProgress 累加字节，这里按固定间隔取差值算瞬时速率
        long[] sample = new long[2];   // [0]=累计字节 [1]=上次采样时的累计
        var runClock = Stopwatch.StartNew();
        System.Threading.Timer? sampler = null;
        if (sampleIntervalMs > 0 && onSample is not null)
            sampler = new System.Threading.Timer(_ =>
            {
                long now = Interlocked.Read(ref sample[0]);
                long delta = now - Interlocked.Exchange(ref sample[1], now);
                onSample(new LiveSample(runClock.Elapsed.TotalSeconds, now,
                    delta / (sampleIntervalMs / 1000.0) / (1024.0 * 1024.0)));
            }, null, sampleIntervalMs, sampleIntervalMs);

        try
        {
            log?.Invoke("填充测试数据（" + pattern + "，" + Decode.UnitConverter.FormatBytes(fileSize) + "）…");
            string? prepErr = file.Prepare(pattern);
            if (prepErr is not null) return new BenchResult(false, prepErr, path, fileSize, pattern, runs, [], sw.Elapsed.TotalSeconds);

            foreach (var row in profile.Rows)
            {
                if (ct.IsCancellationRequested) break;
                // 混合比例：行级优先，其次档位级；都没设就按"是否包含写测试"跑纯读/纯写
                int mix = row.WritePercent >= 0 ? row.WritePercent : profile.WritePercent;
                foreach (var mode in mix >= 0 ? new[] { "mix" }
                                              : includeWrite ? new[] { "read", "write" } : new[] { "read" })
                {
                    if (ct.IsCancellationRequested) break;
                    double best = 0, iops = 0, lat = 0; long bytes = 0; string? rowErr = null;
                    for (int r = 0; r < Math.Max(1, runs); r++)
                    {
                        // 取消令牌必须真的传下去，否则界面上的「中止」是个死按钮
                        var res = file.Run(write: mode == "write", totalBytes: fileSize,
                            queueDepth: row.QueueDepth, random: row.Random, span: fileSize,
                            blockSize: row.BlockSize, threads: row.Threads, ct: ct,
                            durationSeconds: durationSeconds,
                            writePercent: mode == "mix" ? mix : -1,
                            onProgress: sampleIntervalMs > 0 ? n => Interlocked.Add(ref sample[0], n) : null);
                        if (!res.Success) { rowErr = res.Error; break; }
                        if (res.MibPerSecond > best)
                        {
                            best = res.MibPerSecond; iops = res.Iops; lat = res.AvgLatencyMs; bytes = res.Bytes;
                        }
                    }
                    string modeLabel = mode == "mix" ? "mix(" + mix + "%写)" : mode;
                    log?.Invoke("  " + row.Label + " " + modeLabel + " → " + best.ToString("N1") + " MB/s"
                        + "  " + iops.ToString("N0") + " IOPS  " + lat.ToString("0.000") + " ms");
                    results.Add(new BenchRowResult(row.Label, modeLabel, rowErr is null, rowErr,
                        best, iops, lat, bytes, 0));
                }
            }
        }
        finally
        {
            sampler?.Dispose();
            file.Dispose();
        }

        sw.Stop();
        // 被中止时也返回已完成的部分结果，界面能显示"跑到哪一档被停"
        string? cancelErr = ct.IsCancellationRequested ? "已中止（完成 " + results.Count + " 档）" : null;
        return new BenchResult(cancelErr is null, cancelErr, path, fileSize, pattern, runs, results, sw.Elapsed.TotalSeconds);
    }

    /// <summary>解析 "256M" / "1G" / "1048576" 这类尺寸。</summary>
    public static long ParseSize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 256L * 1024 * 1024;
        s = s.Trim().ToUpperInvariant();
        long mul = 1;
        if (s.EndsWith('K')) { mul = 1024; s = s[..^1]; }
        else if (s.EndsWith('M')) { mul = 1024 * 1024; s = s[..^1]; }
        else if (s.EndsWith('G')) { mul = 1024 * 1024 * 1024; s = s[..^1]; }
        return long.TryParse(s, out var v) ? v * mul : 256L * 1024 * 1024;
    }
}
