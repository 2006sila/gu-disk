using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using GuDisk.Core.Interop;
using static GuDisk.Core.Interop.AsyncIo;
using static GuDisk.Core.Interop.NativeMethods;

namespace GuDisk.Core.Benchmark;

/// <summary>一次测速的原始结果。</summary>
public sealed record IoRunResult(bool Success, string? Error, long Bytes, double Seconds, int Ops)
{
    public double BytesPerSecond => Seconds > 0 ? Bytes / Seconds : 0;
    public double MibPerSecond => BytesPerSecond / (1024d * 1024d);
    public double Iops => Seconds > 0 ? Ops / Seconds : 0;
    public double AvgLatencyMs => Ops > 0 ? Seconds * 1000 / Ops : 0;
}

/// <summary>
/// 真 Direct I/O：<c>FILE_FLAG_NO_BUFFERING | FILE_FLAG_OVERLAPPED | FILE_FLAG_WRITE_THROUGH</c>。
///
/// 这一点是专门跟参照软件拉开差距的地方 —— SMART Doctor 的"Direct I/O"其实只用了
/// <c>FileOptions.WriteThrough</c>，没有 NO_BUFFERING，结果会被系统缓存影响。
///
/// 队列深度用 OVERLAPPED + 事件真并发提交：缓冲区一次性按 QD 切片分配（各自独立），
/// OVERLAPPED 数组用 GCHandle 固定，保证内核在异步完成期间看到的地址不变。
/// </summary>
public sealed class DirectIoFile : IDisposable
{
    private readonly SafeFileHandle _h;
    private readonly bool _created;

    public string Path { get; }
    public long Length { get; }
    public uint BlockSize { get; }

    private DirectIoFile(SafeFileHandle h, string path, long length, uint blockSize, bool created)
        => (_h, Path, Length, BlockSize, _created) = (h, path, length, blockSize, created);

    /// <summary>建测试文件并预分配（不写数据，写测试会自己填）。</summary>
    public static (DirectIoFile? File, string? Error) Create(string path, long length, uint blockSize)
    {
        uint flags = FILE_FLAG_OVERLAPPED | FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH;
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);   // 必须先建目录，否则 ERROR_PATH_NOT_FOUND(3)

        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, IntPtr.Zero,
            CREATE_ALWAYS, flags, IntPtr.Zero);

        // **降级重试**：FILE_FLAG_WRITE_THROUGH 要求把每次写都穿透到介质，
        // 部分 USB 桥接固件 / 文件系统过滤驱动会直接拒绝这个打开方式（Win32 5 拒绝访问），
        // 而同一目录用普通方式完全可写。去掉 WRITE_THROUGH 只损失"写穿透"语义，
        // 测速仍是 NO_BUFFERING 直接 I/O（那才是测速的关键）。
        if (h.IsInvalid && (flags & FILE_FLAG_WRITE_THROUGH) != 0)
        {
            h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, IntPtr.Zero,
                CREATE_ALWAYS, flags & ~FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
        }
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            string hint = err == 5 ? "（拒绝访问：若在受限环境里运行，目标目录可能不可写）" : "";
            // 把实际传给 CreateFileW 的参数一并带出 —— 只报 "Win32 5" 时无从判断
            // 是权限、路径、还是标志组合的问题（实测排查 USB 盘测速时卡在这里）。
            return (null, "创建测试文件失败（Win32 " + err + "）" + hint + "：" + path
                + " [access=0x" + (GENERIC_READ | GENERIC_WRITE).ToString("X8")
                + " share=" + FILE_SHARE_READ
                + " disp=" + CREATE_ALWAYS
                + " flags=0x" + flags.ToString("X8") + "]"
                + " 目录可写=" + CanWriteDir(dir)
                + " 管理员=" + IsAdmin());
        }

        // 预分配到目标大小；无缓存 I/O 下首个写入会自动分配簇，读测试前必须先写一遍
        if (!SetEndOfFile(h)) { }
        SetFilePointerEx(h, length, out _, 0);
        SetEndOfFile(h);

        return (new DirectIoFile(h, path, length, blockSize, true), null);
    }

    /// <summary>目录可写性探测（诊断用：区分"目录不可写"与"标志组合被拒"）。</summary>
    private static string CanWriteDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return "?";
        try
        {
            string probe = System.IO.Path.Combine(dir, ".gd_probe_" + Guid.NewGuid().ToString("N")[..6]);
            using (var fs2 = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                       FileOptions.DeleteOnClose))
                fs2.WriteByte(0);
            return "是";
        }
        catch (Exception ex) { return "否(" + ex.GetType().Name + ")"; }
    }

    private static string IsAdmin()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var pr = new System.Security.Principal.WindowsPrincipal(id);
            return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) ? "是" : "否";
        }
        catch { return "?"; }
    }

    public static (DirectIoFile? File, string? Error) Open(string path, uint blockSize)
    {
        uint flags = FILE_FLAG_OVERLAPPED | FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH;
        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, IntPtr.Zero,
            OPEN_EXISTING, flags, IntPtr.Zero);
        if (h.IsInvalid)
            return (null, "打开测试文件失败（Win32 " + Marshal.GetLastWin32Error() + "）：" + path);
        long len = new FileInfo(path).Length;
        return (new DirectIoFile(h, path, len, blockSize, false), null);
    }

    /// <summary>
    /// 跑一轮 I/O。
    ///
    /// **线程与队列的语义与 CrystalDiskMark 对齐**：CDM 的 "Q32T16" 是 16 个线程、每线程 32 个在途请求，
    /// 也就是 512 个并发 —— 早期只做单线程 32 并发时，4K 随机的 IOPS 只有对标值的 1/3.5。
    /// </summary>
    /// <param name="durationSeconds">> 0 时按时间跑（跑够这么多秒就停），否则按 totalBytes 跑完。</param>
    /// <param name="writePercent">0-100：混合读写里写的比例。-1 = 用 write 参数决定纯读/纯写。</param>
    /// <param name="onProgress">每完成一批回调一次（传这批的字节数），用于聚合成实时速率。</param>
    /// <summary>本次运行的统一时间基准（供"按时间跑"判断退出）。</summary>
    private readonly Stopwatch _runClock = Stopwatch.StartNew();

    public IoRunResult Run(bool write, long totalBytes, int queueDepth, bool random, long? span = null,
        uint? blockSize = null, int threads = 1, int timeoutMs = 60000, CancellationToken ct = default,
        int durationSeconds = 0, int writePercent = -1, Action<long>? onProgress = null)
    {
        uint bs = blockSize ?? BlockSize;
        if (bs == 0) bs = 4096;
        if (queueDepth < 1) queueDepth = 1;
        if (threads < 1) threads = 1;

        long region = span ?? Math.Min(Length, totalBytes);
        if (region < bs) region = bs;
        long perThread = Math.Max(bs * queueDepth, totalBytes / threads);

        long bytes = 0; int ops = 0; string? error = null;
        var gate = new object();
        // 计时基准是实例字段（worker 共享），**每轮必须重置** ——
        // 否则第一档跑满时长后，后面每一档一进循环就判定"已超时"直接返回 0。
        _runClock.Restart();
        var sw = Stopwatch.StartNew();

        if (threads == 1)
        {
            var one = RunWorker(write, perThread, queueDepth, random, region, bs, 0, timeoutMs, 0, ct,
                durationSeconds, writePercent, onProgress);
            sw.Stop();
            return new IoRunResult(one.Error is null, one.Error, one.Bytes, sw.Elapsed.TotalSeconds, one.Ops);
        }

        Parallel.For(0, threads, t =>
        {
            var r = RunWorker(write, perThread, queueDepth, random, region, bs, t, timeoutMs, 0, ct,
                durationSeconds, writePercent, onProgress);
            lock (gate)
            {
                bytes += r.Bytes; ops += r.Ops;
                if (r.Error is not null && error is null) error = r.Error;
            }
        });
        sw.Stop();
        return new IoRunResult(error is null, error, bytes, sw.Elapsed.TotalSeconds, ops);
    }

    private sealed record WorkerResult(long Bytes, int Ops, string? Error);

    /// <summary>单线程：提交 queueDepth 个在途请求 → 全部等待完成 → 下一批。</summary>

    private WorkerResult RunWorker(bool write, long totalBytes, int queueDepth, bool random,
        long region, uint bs, int threadIndex, int timeoutMs, long startOffset = 0, CancellationToken ct = default,
        int durationSeconds = 0, int writePercent = -1, Action<long>? onProgress = null)
    {
        int batchBytes = checked((int)(bs * queueDepth));
        IntPtr buffer = AllocAligned(batchBytes);
        if (buffer == IntPtr.Zero) return new WorkerResult(0, 0, "分配对齐缓冲失败");

        var ovs = new OVERLAPPED[queueDepth];
        var events = new IntPtr[queueDepth];
        GCHandle pin = default;
        var rng = new Random(12345 + threadIndex * 7919);
        long done = 0; int ops = 0;

        try
        {
            pin = GCHandle.Alloc(ovs, GCHandleType.Pinned);   // 异步期间地址必须固定
            for (int i = 0; i < queueDepth; i++)
            {
                events[i] = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
                if (events[i] == IntPtr.Zero) return new WorkerResult(0, 0, "创建事件失败");
                ovs[i] = new OVERLAPPED { hEvent = events[i] };
            }

            // 按时间跑时批次数不设上限，靠下方的用时判断退出
            long batches = durationSeconds > 0 ? long.MaxValue : Math.Max(1, totalBytes / batchBytes);
            bool mixed = writePercent is >= 0 and <= 100;
            for (long b = 0; b < batches; b++)
            {
                if (ct.IsCancellationRequested) return new WorkerResult(done, ops, "已中止");
                if (durationSeconds > 0 && _runClock.Elapsed.TotalSeconds >= durationSeconds) break;
                for (int i = 0; i < queueDepth; i++)
                {
                    long blocks = Math.Max(1, region / bs);
                    long blockIndex = random
                        ? (long)(rng.NextDouble() * blocks)
                        : ((b * queueDepth + i) % blocks);
                    long offset = random ? blockIndex * bs : startOffset + (b * queueDepth + i) * bs;
                    if (offset + bs > Length) offset = Math.Max(0, Length - bs);

                    IntPtr slice = IntPtr.Add(buffer, (int)(i * bs));
                    ovs[i].OffsetLow = (int)(offset & 0xFFFFFFFF);
                    ovs[i].OffsetHigh = (int)(offset >> 32);
                    ResetEvent(events[i]);

                    // 混合模式：每个 I/O 单独掷骰子决定读还是写（与 CDM 的 TestMix 同义）
                    bool thisWrite = mixed ? rng.Next(100) < writePercent : write;
                    bool ok = thisWrite
                        ? WriteFile(_h, slice, bs, out _, ref ovs[i])
                        : ReadFile(_h, slice, bs, out _, ref ovs[i]);
                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != ERROR_IO_PENDING)
                            return new WorkerResult(done, ops, (thisWrite ? "WriteFile" : "ReadFile") + " 失败 Win32=" + err);
                    }
                }

                uint w = WaitForMultipleObjects((uint)queueDepth, events, true, (uint)timeoutMs);
                if (w == WAIT_TIMEOUT) return new WorkerResult(done, ops, "I/O 超时");
                if (w != WAIT_OBJECT_0) return new WorkerResult(done, ops, "等待失败 code=" + w);

                for (int i = 0; i < queueDepth; i++)
                    if (!GetOverlappedResult(_h, ref ovs[i], out uint moved, false) || moved != bs)
                        return new WorkerResult(done, ops, "完成结果异常（传输 " + moved + " 字节，期望 " + bs + "）");

                done += (long)bs * queueDepth;
                ops += queueDepth;
                onProgress?.Invoke((long)bs * queueDepth);
            }
            return new WorkerResult(done, ops, null);
        }
        finally
        {
            foreach (var e in events) if (e != IntPtr.Zero) CloseHandle(e);
            if (pin.IsAllocated) pin.Free();
            FreeAligned(buffer);
        }
    }
    /// <summary>以只读方式打开裸设备（\\.\PhysicalDriveN）。裸盘读需要管理员权限。</summary>
    public static (DirectIoFile? File, string? Error) OpenRaw(string devicePath, long length, uint blockSize)
    {
        uint flags = FILE_FLAG_OVERLAPPED | FILE_FLAG_NO_BUFFERING;
        var h = CreateFileW(devicePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
            OPEN_EXISTING, flags, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            string msg = err == 5
                ? "打开裸设备需要管理员权限（Win32 5）：" + devicePath
                : "打开裸设备失败（Win32 " + err + "）：" + devicePath;
            return (null, msg);
        }
        return (new DirectIoFile(h, devicePath, length, blockSize, false), null);
    }

    /// <summary>
    /// 单块读（QD1），**返回 Win32 错误码** —— 坏道扫描靠它把失败精确定位到一个块。
    /// 批式读吞掉了错误码，所以扫描命中失败时必须回退到这个粒度重读。
    /// </summary>
    public (bool Ok, int Win32Error, double MibPerSecond) ReadChunk(long offset, uint bytes, int timeoutMs = 20000)
    {
        IntPtr buf = AllocAligned((int)bytes);
        IntPtr ev = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        if (buf == IntPtr.Zero || ev == IntPtr.Zero)
        {
            if (buf != IntPtr.Zero) FreeAligned(buf);
            if (ev != IntPtr.Zero) CloseHandle(ev);
            return (false, -1, 0);
        }
        try
        {
            var ov = new OVERLAPPED
            {
                hEvent = ev,
                OffsetLow = (int)(offset & 0xFFFFFFFF),
                OffsetHigh = (int)(offset >> 32),
            };
            var sw = Stopwatch.StartNew();
            if (!ReadFile(_h, buf, bytes, out _, ref ov))
            {
                int e = Marshal.GetLastWin32Error();
                if (e != ERROR_IO_PENDING) return (false, e, 0);
            }
            if (WaitForMultipleObjects(1, new[] { ev }, true, (uint)timeoutMs) != WAIT_OBJECT_0)
                return (false, 1460, 0);                       // ERROR_TIMEOUT
            if (!GetOverlappedResult(_h, ref ov, out uint moved, false))
                return (false, Marshal.GetLastWin32Error(), 0);
            sw.Stop();
            double sec = Math.Max(1e-9, sw.Elapsed.TotalSeconds);
            return (true, 0, bytes / (1024d * 1024d) / sec);
        }
        finally { CloseHandle(ev); FreeAligned(buf); }
    }

    /// <summary>从指定偏移顺序读一段（全盘/大文件顺序曲线用），返回该段的吞吐。</summary>
    /// <param name="timeoutMs">单次批量读的等待上限；超时返回 Win32 1460，扫描器据此判为"超时块"。</param>
    public IoRunResult ReadRange(long startOffset, long bytes, uint blockSize, int queueDepth = 8,
        int timeoutMs = 60000)
        => RunInternal(false, bytes, queueDepth, false, bytes, blockSize, 1, timeoutMs, startOffset);

    /// <summary>Run 的内部实现（可指定起始偏移，供曲线采样调用）。</summary>
    private IoRunResult RunInternal(bool write, long totalBytes, int queueDepth, bool random,
        long span, uint bs, int threads, int timeoutMs, long startOffset)
    {
        if (bs == 0) bs = 4096;
        if (queueDepth < 1) queueDepth = 1;
        long region = Math.Max(bs, span);
        long perThread = Math.Max((long)bs * queueDepth, totalBytes / Math.Max(1, threads));
        var sw = Stopwatch.StartNew();
        var one = RunWorker(write, perThread, queueDepth, random, region, bs, 0, timeoutMs, startOffset);
        sw.Stop();
        return new IoRunResult(one.Error is null, one.Error, one.Bytes, sw.Elapsed.TotalSeconds, one.Ops);
    }

    /// <summary>用指定数据模式填充整个文件（测数据：0x00 / 0xFF / 随机）。</summary>
    public string? Prepare(string pattern)
    {
        int chunk = (int)Math.Min(BlockSize * 64, 8 * 1024 * 1024);
        IntPtr buf = AllocAligned(chunk);
        if (buf == IntPtr.Zero) return "分配填充缓冲失败";
        var ov = new OVERLAPPED { hEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero) };
        try
        {
            var bytes = new byte[chunk];
            switch (pattern)
            {
                case "zero": break;
                case "one": Array.Fill(bytes, (byte)0xFF); break;
                default:
                    var rng = new Random(20261012);
                    rng.NextBytes(bytes);
                    break;
            }
            Marshal.Copy(bytes, 0, buf, chunk);

            for (long off = 0; off < Length; off += chunk)
            {
                int len = (int)Math.Min(chunk, Length - off);
                len -= len % (int)BlockSize;               // 无缓存 I/O 必须整块
                if (len <= 0) break;
                ov.OffsetLow = (int)(off & 0xFFFFFFFF);
                ov.OffsetHigh = (int)(off >> 32);
                ResetEvent(ov.hEvent);
                if (!WriteFile(_h, buf, (uint)len, out _, ref ov))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_IO_PENDING) return "填充失败 Win32=" + err;
                }
                if (WaitForMultipleObjects(1, [ov.hEvent], true, 30000) != WAIT_OBJECT_0) return "填充超时";
            }
            return null;
        }
        finally
        {
            if (ov.hEvent != IntPtr.Zero) CloseHandle(ov.hEvent);
            FreeAligned(buf);
        }
    }

    /// <summary>对齐到物理扇区要求的块大小（4 KiB 对 SSD / 512e 都安全）。</summary>
    public static uint NormalizeBlockSize(long requested) => (uint)Math.Max(4096, requested);
    public static long RoundUp(long v, long align) => (v + align - 1) / align * align;

    public void Dispose()
    {
        _h.Dispose();
        if (_created)
        {
            try { if (File.Exists(Path)) File.Delete(Path); } catch { }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ResetEvent(IntPtr hEvent);
}
