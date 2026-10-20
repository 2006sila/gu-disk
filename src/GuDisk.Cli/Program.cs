using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using GuDisk.Core;
using GuDisk.Core.App;

namespace GuDisk.Cli;

/// <summary>
/// Gu-disk 命令行。用途：脚本验证、沙箱内自检、无界面批量取数。
/// M0 提供 version / policy --selftest；list·info·smart 在 M1 接入设备枚举。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "version" => CmdVersion(),
            "policy" => args.Contains("--selftest") ? CmdSelfTest() : CmdVersion(),
            "list" => CmdList(args),
            "diag" => CmdDiag(args),
            "info" => CmdInfo(args),
            "smart" => CmdSmart(args),
            "probe" => CmdSmart(args.Append("--probe").ToArray()),
            "health" => CmdHealth(args),
            "history" => CmdHistory(args),
            "import-cdi" => CmdImportCdi(args),
            "selftest" => CmdSelfTest(args),
            "aam-apm" => CmdAamApm(args),
            "bench" => CmdBench(args),
            "curve" => CmdCurve(args),
            "update-db" => CmdUpdateDb(args),
            "scan" => CmdScan(args),
            "report" => CmdReport(args),
            _ => Unknown(args[0])
        };
    }

    private static void PrintHelp()
    {
        Console.WriteLine(GuDiskInfo.ProductName + " " + GuDiskInfo.Version + " · " + GuDiskInfo.ProductSubtitle);
        Console.WriteLine();
        Console.WriteLine("用法: Gu-disk.Cli <命令> [参数]");
        Console.WriteLine();
        Console.WriteLine("  version                    版本信息");
        Console.WriteLine("  policy --selftest          环境自检（路径 / 权限 / smartctl / 特征库）");
        Console.WriteLine("  list [--json]              列出物理磁盘                   [M1]");
        Console.WriteLine("  info <n|serial> [--json]   设备识别详情                   [M1]");
        Console.WriteLine("  smart <n|serial> [--json] [--probe] [--protocol auto|ata|scsi|nvme] [--no-check]");
        Console.WriteLine("  probe <n|serial>           协议探测（= smart --probe）");
        Console.WriteLine("  selftest <n> --type short|extended|abort                 [M5]");
        Console.WriteLine("  bench --target C: --profile cdm4 --size 128M             [M3]");
        Console.WriteLine("  scan --target 2 --mode readonly                          [M4]");
        Console.WriteLine("  report <n> --format html|txt|json --out <path>           [M5]");
    }

    private static int CmdVersion()
    {
        Console.WriteLine(GuDiskInfo.ProductName + " " + GuDiskInfo.Version + " (" + GuDiskInfo.Stage + ")");
        Console.WriteLine("运行时: " + RuntimeInformation.FrameworkDescription);
        Console.WriteLine("数据根: " + AppPaths.Root);
        Console.WriteLine("产品数据: " + AppPaths.ProductData);
        return 0;
    }

    // ---------------- smart / probe ----------------
    private static int CmdSmart(string[] args)
    {
        var drives = Enumerate();
        var d = Pick(drives, args);
        if (d is null) return 1;

        bool probe = args.Contains("--probe") || args.Contains("probe");
        string protocol = "auto";
        int pi = Array.IndexOf(args, "--protocol");
        if (pi >= 0 && pi + 1 < args.Length) protocol = args[pi + 1];
        if (args.Contains("--no-check")) { /* 保留：只跑原生不做交叉校验 */ }
        var outcome = new GuDisk.Core.Smart.SmartReadOrchestrator().Read(d, !args.Contains("--no-check"), protocol);

        // **套用特征库把属性名换成中文** —— 以前 CLI 直接打印编排器的原始结果，
        // 绕过 DriveDiagnostics 的 ApplyNames，于是机械盘显示的是 smartctl 的英文名
        // （Raw_Read_Error_Rate），而 GUI 同一条属性是中文。CLI 是诊断工具，必须一致。
        if (outcome.Nvme is null && outcome.Attributes.Count > 0
            && GuDisk.Core.Decode.SmartAttributeDb.Load() is { } attrDb)
        {
            var attrList = outcome.Attributes.ToList();
            attrDb.ApplyNames(attrList, d.Model, false);
            outcome = outcome with { Attributes = attrList };
        }

        if (args.Contains("--json"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                drive = d,
                engine = outcome.Engine,
                success = outcome.Success,
                error = outcome.Error,
                source = outcome.Source,
                nvme = outcome.Nvme,
                attributes = outcome.Attributes,
                attempts = outcome.Attempts,
                crossCheck = outcome.CrossCheck
            }, JsonOpts));
            return outcome.Success ? 0 : 1;
        }

        Console.WriteLine();
        Console.WriteLine("== " + d.Model + "  ·  " + d.DevicePath + " ==");

        if (!outcome.Success)
        {
            Console.Error.WriteLine("x 读取失败: " + outcome.Error);
            PrintAttempts(outcome.Attempts);
            return 1;
        }

        Console.WriteLine("   引擎: " + outcome.Engine + "    数据源: " + outcome.Source);
        Console.WriteLine();
        // 与 CrystalDiskInfo 对齐：**当前值 / 最差值 / 临界值** 三列是判断"还剩多少余量"的关键，
        // 以前只打原始值（对应 CDI 的最右一列），信息少了一大截。
        // NVMe 属性没有阈值概念（其 Current/Worst/Threshold 恒为 0），显示 "-" 而不是骗人的 0。
        Console.WriteLine("ID   属性名称                      当前 最差 临界  原始值                                  状态");
        Console.WriteLine(new string((char)45, 134));
        foreach (var a in outcome.Attributes)
        {
            bool hasTh = a.Current != 0 || a.Worst != 0 || a.Threshold != 0;
            Console.WriteLine(string.Format("{0,-4} {1,-26} {2,4} {3,4} {4,4}  {5,-36} {6}",
                a.Id.ToString("X2"), a.NameCn,
                hasTh ? a.Current.ToString() : "-",
                hasTh ? a.Worst.ToString() : "-",
                hasTh ? a.Threshold.ToString() : "-",
                Trunc(a.RawMeaning, 34), a.Status));
        }

        if (probe) PrintAttempts(outcome.Attempts);

        if (outcome.CrossCheck.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("-- 双引擎交叉校验（原生 vs smartctl）--");
            foreach (var c in outcome.CrossCheck)
                Console.WriteLine("  " + (c.Agree ? "[一致] " : "[不一致] ") + c.Field.PadRight(22)
                    + c.Native.PadRight(18) + " | " + c.Smartctl
                    + (c.Note is null ? "" : "   （" + c.Note + "）"));
            int bad = outcome.CrossCheck.Count(x => !x.Agree);
            Console.WriteLine("  合计 " + outcome.CrossCheck.Count + " 项，" + (bad == 0 ? "全部一致" : bad + " 项不一致"));
        }

        Console.WriteLine();
        Console.WriteLine("-- 概要 --");
        if (outcome.Nvme is not null)
            foreach (var (k, v) in GuDisk.Core.Smart.SmartProbe.Summarize(outcome.Nvme))
                Row(k, v);
        return 0;
    }

    // ---------------- bench ----------------
    private static int CmdBench(string[] args)
    {
        string target = "C:";
        int ti = Array.IndexOf(args, "--target");
        if (ti >= 0 && ti + 1 < args.Length) target = args[ti + 1];

        string profileName = "cdm4";
        int pi = Array.IndexOf(args, "--profile");
        if (pi >= 0 && pi + 1 < args.Length) profileName = args[pi + 1];

        string sizeArg = "256M";
        int si = Array.IndexOf(args, "--size");
        if (si >= 0 && si + 1 < args.Length) sizeArg = args[si + 1];
        long size = GuDisk.Core.Benchmark.BenchRunner.ParseSize(sizeArg);

        string pattern = "random";
        int ai = Array.IndexOf(args, "--pattern");
        if (ai >= 0 && ai + 1 < args.Length) pattern = args[ai + 1];

        int runs = 5;   // 与 CrystalDiskMark 默认一致：跑 5 次取最好
        int ri = Array.IndexOf(args, "--runs");
        if (ri >= 0 && ri + 1 < args.Length && int.TryParse(args[ri + 1], out var rv)) runs = rv;

        bool write = args.Contains("--write");
        var profile = profileName.ToLowerInvariant() switch
        {
            "quick" => GuDisk.Core.Benchmark.BenchProfiles.Quick,
            "atto" => GuDisk.Core.Benchmark.BenchProfiles.Atto,
            "access" => GuDisk.Core.Benchmark.BenchProfiles.AccessTime,
            _ => GuDisk.Core.Benchmark.BenchProfiles.Cdm4
        };

        Console.WriteLine();
        Console.WriteLine("== 基准测速 ==");
        // 混合读写比例与"按时间跑"（0 = 按 I/O 量）
        int mix = -1;
        int mixIdx = Array.IndexOf(args, "--mix");
        if (mixIdx >= 0 && mixIdx + 1 < args.Length && int.TryParse(args[mixIdx + 1], out var mv))
            mix = Math.Clamp(mv, 0, 100);
        if (mix >= 0) profile = profile with { WritePercent = mix };

        int duration = 0;
        int durIdx = Array.IndexOf(args, "--time");
        if (durIdx >= 0 && durIdx + 1 < args.Length && int.TryParse(args[durIdx + 1], out var dv))
            duration = Math.Clamp(dv, 1, 600);

        Console.WriteLine("  目标      " + target);
        Console.WriteLine("  档位      " + profile.Name + "（" + profile.Rows.Count + " 行）");
        Console.WriteLine("  文件大小  " + GuDisk.Core.Decode.UnitConverter.FormatBytes(size));
        Console.WriteLine("  测试数据  " + pattern + "    运行次数 " + runs);
        Console.WriteLine("  写测试    " + (write ? "开启（会真写盘）" : "关闭（只读）"));
        Console.WriteLine("  引擎      FILE_FLAG_NO_BUFFERING | OVERLAPPED | WRITE_THROUGH");
        Console.WriteLine();

        var res = GuDisk.Core.Benchmark.BenchRunner.Run(target, size, profile, pattern, runs, write,
            keepFile: args.Contains("--keep"), log: m => Console.WriteLine("  " + m),
            durationSeconds: duration);

        if (mix >= 0) Console.WriteLine("  混合      写 " + mix + "%（与 CDM 的 TestMix 同义）");
        if (duration > 0) Console.WriteLine("  时长      每档跑 " + duration + " 秒");
        if (!res.Success) { Console.Error.WriteLine("x 测速失败: " + res.Error); return 1; }

        if (args.Contains("--json"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(res, JsonOpts));
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("档位".PadRight(14) + "模式   吞吐量           IOPS        平均延迟");
        Console.WriteLine(new string((char)45, 74));
        foreach (var row in res.Results)
            Console.WriteLine(row.Label.PadRight(14) + row.Mode.PadRight(7)
                + (row.Success ? row.MibPerSecond.ToString("N1") + " MB/s" : "失败: " + row.Error).PadRight(16)
                + row.Iops.ToString("N0").PadLeft(9) + "   " + row.LatencyMs.ToString("0.000") + " ms");

        Console.WriteLine();
        Console.WriteLine("  测试文件: " + res.Target + (args.Contains("--keep") ? "（保留）" : "（已删除）"));
        Console.WriteLine("  总耗时:   " + res.TotalSeconds.ToString("0.0") + " s");
        return 0;
    }
    // ---------------- update-db ----------------
    private static int CmdUpdateDb(string[] args)
    {
        Console.WriteLine();
        Console.WriteLine("== 在线更新驱动特征库（drivedb.h）==");
        Console.WriteLine("  来源  smartmontools 官方（GitHub raw → jsDelivr 镜像）");
        Console.WriteLine();

        if (args.Contains("--probe"))
        {
            var pr = GuDisk.Core.Decode.DrivedbUpdater.ProbeAsync().GetAwaiter().GetResult();
            Console.WriteLine(pr.Success
                ? "  可达：" + pr.Url + "（" + pr.Bytes + " 字节）"
                : "  不可达：" + pr.Error);
            return pr.Success ? 0 : 1;
        }

        var r = GuDisk.Core.Decode.DrivedbUpdater.UpdateAsync(m => Console.WriteLine("  " + m))
            .GetAwaiter().GetResult();
        Console.WriteLine();
        if (!r.Success) { Console.Error.WriteLine("x " + r.Error); return 1; }
        Console.WriteLine("  已下载 " + r.Bytes + " 字节，版本 " + r.Version);
        Console.WriteLine("  索引重建：" + (r.IndexRebuilt ? "完成" : "未完成"));
        if (r.ConverterOutput is { Length: > 0 })
            Console.WriteLine("  " + r.ConverterOutput.Replace(Environment.NewLine, Environment.NewLine + "  "));
        return 0;
    }

    // ---------------- report ----------------
    private static int CmdReport(string[] args)
    {
        var drives = Enumerate();
        var d = Pick(drives, args);
        if (d is null) return 1;

        string format = "html";
        int fi = Array.IndexOf(args, "--format");
        if (fi >= 0 && fi + 1 < args.Length) format = args[fi + 1].ToLowerInvariant();

        string? outPath = null;
        int oi = Array.IndexOf(args, "--out");
        if (oi >= 0 && oi + 1 < args.Length) outPath = args[oi + 1];
        outPath ??= Path.Combine(Environment.CurrentDirectory,
            "Gu-disk-report-" + d.DeviceNumber + "." + (format == "txt" ? "txt" : format));

        Console.WriteLine();
        Console.WriteLine("== 生成诊断报告 ==");
        Console.WriteLine("  设备   " + d.Model);
        Console.WriteLine("  格式   " + format);

        var diag = GuDisk.Core.App.DriveDiagnostics.Run(d, crossCheck: !args.Contains("--no-check"));
        Console.WriteLine("  判定   " + diag.Verdict.Label + " · " + diag.Verdict.CountsText
            + " · 引擎 " + diag.Smart.Engine);

        GuDisk.Core.Benchmark.BenchResult? bench = null;
        if (args.Contains("--bench"))
        {
            Console.WriteLine("  测速   跑 CDM 四档（256 MiB）…");
            string bp = Path.Combine(Path.GetDirectoryName(outPath) ?? ".", "Gu-disk_bench.tmp");
            bench = GuDisk.Core.Benchmark.BenchRunner.Run(bp, 256L * 1024 * 1024,
                GuDisk.Core.Benchmark.BenchProfiles.Cdm4, "random", 1, false, false);
            if (bench.Success) Console.WriteLine("         " + bench.Results[0].MibPerSecond.ToString("N0") + " MB/s …");
        }

        GuDisk.Core.Scan.ScanResult? scan = null;
        if (args.Contains("--scan"))
        {
            Console.WriteLine("  扫描   卷内扫描（1 GiB）…");
            string sp = Path.Combine(Path.GetDirectoryName(outPath) ?? ".", "Gu-disk_scan.tmp");
            scan = GuDisk.Core.Scan.SurfaceScanner.RunFile(sp, 1024L * 1024 * 1024);
            if (scan.Success) Console.WriteLine("         坏块 " + scan.ErrorCount + " · 偏慢 " + scan.SlowCount);
        }

        // 温度历史：从历史库读（含从 CDI 导入的）
        IReadOnlyList<(DateTime, double)> temp = [];
        try
        {
            var hist = new GuDisk.Core.History.HistoryStore();
            temp = hist.Query(d.Model, d.Serial, "Temperature")
                       .Select(x => (x.When, x.Value)).ToList();
        }
        catch { }

        var input = new GuDisk.Core.Report.ReportInput(diag, bench, null, scan,
            temp.Count > 0 ? temp : null);

        string content = format switch
        {
            "txt" => GuDisk.Core.Report.ReportWriter.ToText(input),
            "json" => GuDisk.Core.Report.ReportWriter.ToJson(input),
            _ => GuDisk.Core.Report.ReportWriter.ToHtml(input)
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, content, new System.Text.UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine("  已写入 " + outPath + "   " + (content.Length / 1024) + " KB");
        if (format == "html") Console.WriteLine("  用浏览器打开即可；样式与图表全部内嵌，单文件可直接转发。");
        return 0;
    }

    // ---------------- selftest ----------------
    private static int CmdSelfTest(string[] args)
    {
        var drives = Enumerate();
        var d = Pick(drives, args);
        if (d is null) return 1;

        string type = "short";
        int ti = Array.IndexOf(args, "--type");
        if (ti >= 0 && ti + 1 < args.Length) type = args[ti + 1].ToLowerInvariant();

        Console.WriteLine();
        Console.WriteLine("== 硬件自检 · " + d.Model + " ==");
        Console.WriteLine();

        bool wantLog = args.Contains("--log") || args.Contains("--errors") || !args.Contains("--start");
        if (args.Contains("--start") || args.Contains("--type"))
        {
            var kind = type switch
            {
                "extended" => GuDisk.Core.Smart.SelfTestKind.Extended,
                "abort" => GuDisk.Core.Smart.SelfTestKind.Abort,
                _ => GuDisk.Core.Smart.SelfTestKind.Short
            };
            var r = GuDisk.Core.Smart.SelfTest.Start(d, kind);
            Console.WriteLine(r.Success ? "  已下发: " + r.Message : "  x 失败: " + r.Error);
            Console.WriteLine("  通道: " + r.Transport);
            Console.WriteLine();
        }

        var (okLog, errLog, entries, raw) = GuDisk.Core.Smart.SelfTest.ReadSelfTestLog(d);
        Console.WriteLine("-- 自检日志" + (okLog ? "（" + entries.Count + " 条）" : "（读取失败）") + " --");
        if (!okLog) Console.WriteLine("  " + errLog);
        else if (entries.Count == 0) Console.WriteLine("  尚无自检记录");
        foreach (var e in entries)
            Console.WriteLine("  #" + e.Number + "  " + e.Type + "  " + e.Status
                + "  剩余 " + e.RemainingPercent + "%  通电 " + e.PowerOnHours + " 小时"
                + (e.Lba > 0 ? "  LBA " + e.Lba : ""));

        var (okErr, errErr, errs) = GuDisk.Core.Smart.SelfTest.ReadErrorLog(d);
        Console.WriteLine();
        Console.WriteLine("-- 错误日志" + (okErr ? "（" + errs.Count + " 条）" : "（读取失败）") + " --");
        if (!okErr) Console.WriteLine("  " + errErr);
        else if (errs.Count == 0) Console.WriteLine("  无错误记录");
        foreach (var e in errs)
            Console.WriteLine("  #" + e.Number + "  错误码 0x" + e.ErrorCode.ToString("X")
                + "  LBA " + e.Lba + (e.Uncorrectable ? "  【不可纠正】" : ""));

        if (args.Contains("--json"))
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { entries, errors = errs }, JsonOpts));
        return okLog ? 0 : 1;
    }

    // ---------------- aam-apm ----------------
    private static int CmdAamApm(string[] args)
    {
        var drives = Enumerate();
        var d = Pick(drives, args);
        if (d is null) return 1;

        Console.WriteLine();
        Console.WriteLine("== AAM / APM · " + d.Model + " ==");

        int ai = Array.IndexOf(args, "--aam");
        if (ai >= 0 && ai + 1 < args.Length && int.TryParse(args[ai + 1], out var aamVal))
        {
            var (ok, err, we) = GuDisk.Core.Smart.AamApm.SetAam(d, (byte)aamVal);
            Console.WriteLine("  AAM → " + aamVal + "：" + (ok ? "已应用" : err + "（Win32 " + we + "）"));
        }
        int pi = Array.IndexOf(args, "--apm");
        if (pi >= 0 && pi + 1 < args.Length && int.TryParse(args[pi + 1], out var apmVal))
        {
            var (ok, err, we) = GuDisk.Core.Smart.AamApm.SetApm(d, (byte)apmVal);
            Console.WriteLine("  APM → " + apmVal + "：" + (ok ? "已应用" : err + "（Win32 " + we + "）"));
        }

        var (ok2, err2, v) = GuDisk.Core.Smart.AamApm.Read(d);
        if (!ok2) { Console.WriteLine("  读取失败: " + err2); return 1; }
        Console.WriteLine("  AAM  当前 " + (v!.Aam?.ToString() ?? "不支持")
            + "   厂商推荐 " + (v.AamRecommended?.ToString() ?? "-")
            + "   （0x80 最安静，0xFE 最快）");
        Console.WriteLine("  APM  当前 " + (v.Apm?.ToString() ?? "不支持")
            + "   厂商推荐 " + (v.ApmRecommended?.ToString() ?? "-")
            + "   （0x01 最省电，0xFE 最高性能）");
        return 0;
    }

    // ---------------- scan ----------------
    private static int CmdScan(string[] args)
    {
        string? target = null;
        int ti = Array.IndexOf(args, "--target");
        if (ti >= 0 && ti + 1 < args.Length) target = args[ti + 1];

        int dev = -1;
        int di = Array.IndexOf(args, "--raw");
        if (di >= 0 && di + 1 < args.Length && int.TryParse(args[di + 1], out var dv)) dev = dv;

        long size = 1024L * 1024 * 1024;
        int si = Array.IndexOf(args, "--size");
        if (si >= 0 && si + 1 < args.Length) size = GuDisk.Core.Benchmark.BenchRunner.ParseSize(args[si + 1]);

        string? csv = null;
        int ci = Array.IndexOf(args, "--csv");
        if (ci >= 0 && ci + 1 < args.Length) csv = args[ci + 1];

        Console.WriteLine();
        Console.WriteLine("== 表面扫描（坏道检测）==");

        var lastReport = DateTime.UtcNow;
        var progress = new Progress<GuDisk.Core.Scan.ScanProgress>(p =>
        {
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds < 500) return;
            lastReport = DateTime.UtcNow;
            Console.Error.Write(string.Format("\r  {0,5:0.0}%  {1,9:N0} MB/s  已扫 {2}  {3}  问题块 {4}   ",
                p.Percent, p.MibPerSecond, GuDisk.Core.Decode.UnitConverter.FormatBytes(p.DoneBytes),
                p.Eta is null ? "" : "剩 " + FmtDur(p.Eta.Value), p.ProblemCount));
        });

        GuDisk.Core.Scan.ScanResult res;
        if (dev >= 0)
        {
            Console.WriteLine("  模式  只读扫描整盘 PhysicalDrive" + dev + "（需要管理员，不写任何数据）");
            res = GuDisk.Core.Scan.SurfaceScanner.RunRaw(dev, progress: progress);
        }
        else if (target is not null)
        {
            string p = target;
            if (p.EndsWith(':'))
                p = Path.Combine(p + "\\", "Gu-disk_scan_" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            Console.WriteLine("  模式  扫描测试文件（安全，不碰用户数据）");
            Console.WriteLine("  范围  " + p + "   " + GuDisk.Core.Decode.UnitConverter.FormatBytes(size));
            res = GuDisk.Core.Scan.SurfaceScanner.RunFile(p, size, progress: progress);
        }
        else
        {
            Console.Error.WriteLine("用法: scan --target <盘符|文件路径> [--size 1G] [--csv 输出.csv]");
            Console.Error.WriteLine("      scan --raw <设备号>        （整盘只读扫描，需要管理员）");
            return 2;
        }
        Console.Error.WriteLine();

        if (!res.Success) { Console.Error.WriteLine("x 扫描失败: " + res.Error); return 1; }

        if (args.Contains("--json"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(res, JsonOpts));
            return 0;
        }

        Console.WriteLine("  模式      " + res.Mode);
        Console.WriteLine("  块大小    " + GuDisk.Core.Decode.UnitConverter.FormatBytes(res.BlockSize));
        Console.WriteLine("  已扫描    " + GuDisk.Core.Decode.UnitConverter.FormatBytes(res.ScannedBytes)
            + "（" + res.ScannedBlocks + " 块）");
        Console.WriteLine("  耗时      " + FmtDur(res.Elapsed));
        Console.WriteLine("  速度      平均 " + res.AverageMibPerSecond.ToString("N0")
            + " · 最低 " + res.MinMibPerSecond.ToString("N0")
            + " · 最高 " + res.MaxMibPerSecond.ToString("N0") + " MB/s");
        Console.WriteLine();
        Console.WriteLine("  结论      " + res.Conclusion);
        Console.WriteLine();

        if (res.ProblemBlocks.Count > 0)
        {
            Console.WriteLine("-- 问题块（前 40 条）--");
            Console.WriteLine("序号".PadRight(8) + "偏移".PadRight(20) + "类型".PadRight(9) + "速度        错误码  解释");
            Console.WriteLine(new string((char)45, 110));
            foreach (var b in res.ProblemBlocks.Take(40))
                Console.WriteLine(string.Format("{0,-8}{1,-20}{2,-9}{3,-12}{4,-8}{5}",
                    b.Index, GuDisk.Core.Decode.UnitConverter.FormatBytes(b.OffsetBytes), b.Class,
                    b.MibPerSecond.ToString("N0") + " MB/s", b.Win32Error, b.Explanation));
            if (res.ProblemBlocks.Count > 40)
                Console.WriteLine("  …（共 " + res.ProblemBlocks.Count + " 条，完整列表见 --csv 或 --json）");
        }
        else Console.WriteLine("  未发现任何问题块。");

        if (csv is not null)
        {
            File.WriteAllText(csv, GuDisk.Core.Scan.SurfaceScanner.ToCsv(res), System.Text.Encoding.UTF8);
            Console.WriteLine();
            Console.WriteLine("  CSV 已写入 " + csv);
        }
        return res.ErrorCount > 0 ? 3 : 0;
    }

    // ---------------- curve ----------------
    private static int CmdCurve(string[] args)
    {
        string? target = null;
        int ti = Array.IndexOf(args, "--target");
        if (ti >= 0 && ti + 1 < args.Length) target = args[ti + 1];

        int dev = -1;
        int di = Array.IndexOf(args, "--raw");
        if (di >= 0 && di + 1 < args.Length && int.TryParse(args[di + 1], out var dv)) dev = dv;

        long size = 4L * 1024 * 1024 * 1024;
        int si = Array.IndexOf(args, "--size");
        if (si >= 0 && si + 1 < args.Length) size = GuDisk.Core.Benchmark.BenchRunner.ParseSize(args[si + 1]);

        int samples = 100;
        int mi = Array.IndexOf(args, "--samples");
        if (mi >= 0 && mi + 1 < args.Length && int.TryParse(args[mi + 1], out var sv)) samples = sv;

        Console.WriteLine();
        Console.WriteLine("== 顺序读曲线 ==");

        GuDisk.Core.Benchmark.CurveResult res;
        if (dev >= 0)
        {
            Console.WriteLine("  模式  裸盘直读 PhysicalDrive" + dev + "（需要管理员）");
            res = GuDisk.Core.Benchmark.FullDiskCurve.RunRaw(dev, samples: samples,
                progress: n => { if (n % 10 == 0) Console.Error.Write("."); });
        }
        else if (target is not null)
        {
            string p = target;
            if (p.EndsWith(':'))
                p = Path.Combine(p + "\\", "Gu-disk_curve_" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            Console.WriteLine("  模式  文件顺序读 " + p);
            Console.WriteLine("  大小  " + GuDisk.Core.Decode.UnitConverter.FormatBytes(size) + "    采样 " + samples + " 段");
            res = GuDisk.Core.Benchmark.FullDiskCurve.RunFile(p, size, samples: samples,
                progress: n => { if (n % 10 == 0) Console.Error.Write("."); });
        }
        else
        {
            Console.Error.WriteLine("用法: curve --target <盘符|文件路径> [--size 4G] [--samples 100]");
            Console.Error.WriteLine("      curve --raw <设备号>          （裸盘直读，需要管理员）");
            return 2;
        }
        Console.Error.WriteLine();

        if (!res.Success) { Console.Error.WriteLine("x 曲线失败: " + res.Error); return 1; }

        if (args.Contains("--json"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(res, JsonOpts));
            return 0;
        }

        Console.WriteLine("  " + GuDisk.Core.Benchmark.FullDiskCurve.Summary(res));
        Console.WriteLine();
        // 文本曲线：把采样点画成横向条
        double maxv = Math.Max(1, res.MaxMibPerSecond);
        foreach (var p in res.Points)
        {
            int bars = (int)Math.Round(p.MibPerSecond / maxv * 40);
            Console.WriteLine(string.Format("{0,6:0.0}%  {1,9:N0} MB/s  {2}",
                p.PositionPercent, p.MibPerSecond, new string('#', Math.Max(0, bars))));
        }
        Console.WriteLine();
        Console.WriteLine("  判读: " + res.Diagnosis);
        return 0;
    }

    // ---------------- health ----------------
    private static int CmdHealth(string[] args)
    {
        var drives = Enumerate();
        var d = Pick(drives, args);
        if (d is null) return 1;

        var diag = GuDisk.Core.App.DriveDiagnostics.Run(d, crossCheck: !args.Contains("--no-check"));

        if (args.Contains("--json"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                drive = diag.Drive,
                verdict = diag.Verdict,
                media = diag.Media,
                engine = diag.Smart.Engine,
                crossCheck = diag.CrossCheck,
                attributeDb = diag.AttributeDbSource,
                drivedb = diag.DrivedbSource,
                notes = diag.Notes
            }, JsonOpts));
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("== " + d.Model + "  ·  " + d.DevicePath + " ==");
        Console.WriteLine();
        var v = diag.Verdict;
        Console.WriteLine("  总体状态   " + v.Label + "     （" + v.CountsText + "）");
        Console.WriteLine("  结论       " + v.Summary);
        Console.WriteLine();
        Console.WriteLine("-- 介质类型 --");
        Console.WriteLine("  " + diag.Media.Label + "   置信度 " + diag.Media.ConfidencePercent + "%");
        Console.WriteLine("  依据: " + diag.Media.Reason);
        Console.WriteLine("  TRIM: " + (diag.Drive.NvmeTrim is { } nt ? (nt ? "支持（NVMe DSM）" : "不支持（固件未声明）")
      : diag.Media.TrimSupported ? "支持" : "不支持或未读到"));
        Console.WriteLine();
        Console.WriteLine("-- 判定依据（" + v.Findings.Count + " 项异常）--");
        if (v.Findings.Count == 0) Console.WriteLine("  未发现异常项");
        foreach (var f in v.Findings)
            Console.WriteLine("  [" + GuDisk.Core.Health.StatusVerdictEvaluator.LabelOf(f.Level) + "] "
                + f.Source + " = " + f.RawValue
                + Environment.NewLine + "        " + f.Reading
                + (f.Advice is null ? "" : Environment.NewLine + "        建议: " + f.Advice));
        Console.WriteLine();
        Console.WriteLine("-- 已核对项（" + v.Checks.Count + " 项，均正常）--");
        foreach (var k in v.Checks) Console.WriteLine("  · " + k.Source + " = " + k.RawValue);
        Console.WriteLine();
        Console.WriteLine("-- 引擎 --");
        Console.WriteLine("  " + diag.Smart.Engine + (diag.Smart.Success ? "" : "（读取失败：" + diag.Smart.Error + "）"));
        if (diag.CrossCheck.Count > 0)
        {
            int bad = diag.Disagreements.Count;
            Console.WriteLine("  交叉校验 " + diag.CrossCheck.Count + " 项，" + (bad == 0 ? "全部一致" : bad + " 项不一致"));
        }
        Console.WriteLine("  属性库: " + diag.AttributeDbSource);
        Console.WriteLine("  驱动库: " + diag.DrivedbSource);
        foreach (var n in diag.Notes) Console.WriteLine("  注: " + n);
        Console.WriteLine();
        Console.WriteLine("-- 建议 --");
        var advice = v.Findings.Where(f => f.Advice is not null).Select(f => f.Advice!).Distinct().ToList();
        if (advice.Count == 0) advice.Add("未发现需要处理的问题，按常规备份习惯使用即可。");
        foreach (var r in advice) Console.WriteLine("  · " + r);
        return 0;
    }

    // ---------------- history ----------------
    private static int CmdHistory(string[] args)
    {
        var store = new GuDisk.Core.History.HistoryStore();
        Console.WriteLine();
        Console.WriteLine("历史根: " + store.Root);
        Console.WriteLine();
        var folders = store.ListFolders();
        if (folders.Count == 0) { Console.WriteLine("（暂无历史）先跑 import-cdi 或 health 采样。"); return 0; }
        Console.WriteLine("磁盘目录".PadRight(48) + "指标数  总点数  最近一次");
        Console.WriteLine(new string((char)45, 100));
        foreach (var f in folders)
        {
            var dir = Path.Combine(store.Root, f);
            var csvs = Directory.GetFiles(dir, "*.csv");
            int points = 0; string last = "-";
            foreach (var c in csvs)
            {
                var lines = File.ReadLines(c).ToArray();
                points += lines.Length;
                if (lines.Length > 0)
                {
                    var t = lines[^1].Split(',')[0];
                    if (string.CompareOrdinal(t, last) > 0) last = t;
                }
            }
            Console.WriteLine(Trunc(f, 46).PadRight(48) + csvs.Length.ToString().PadLeft(6)
                + points.ToString().PadLeft(8) + "  " + last);
        }
        return 0;
    }

    // ---------------- import-cdi ----------------
    private static int CmdImportCdi(string[] args)
    {
        string? root = args.Skip(1).FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal));
        root ??= GuDisk.Core.App.AppPaths.FindCdiSmartFolder();
        if (root is null)
        {
            Console.Error.WriteLine("未找到 CrystalDiskInfo 的 Smart 目录，请显式指定：import-cdi <路径>");
            return 2;
        }
        Console.WriteLine();
        Console.WriteLine("从 " + root + " 导入 …");
        var r = new GuDisk.Core.History.CdiHistoryImporter().Import(root);
        Console.WriteLine("  磁盘目录 " + r.Folders + " 个 · CSV " + r.Files + " 个 · 新增数据点 " + r.Points);
        foreach (var n in r.Notes) Console.WriteLine("  注: " + n);
        Console.WriteLine();
        Console.WriteLine("历史根: " + new GuDisk.Core.History.HistoryStore().Root);
        return 0;
    }
    private static void PrintAttempts(IReadOnlyList<GuDisk.Core.Smart.ProbeAttempt> attempts)
    {
        if (attempts.Count == 0) return;
        Console.WriteLine();
        Console.WriteLine("-- 协议探测记录 --");
        foreach (var a in attempts)
            Console.WriteLine("  [" + (a.Success ? "OK  " : "FAIL") + "] " + a.Protocol.PadRight(10)
                + a.ElapsedMs.ToString().PadLeft(5) + " ms  " + a.Command
                + (a.ErrorCode is not null ? "   <" + a.ErrorCode + ": " + a.ErrorMessage + ">" : ""));
    }
    private static string Trunc(string s, int n)
        => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

    // ---------------- diag ----------------
    private static int CmdDiag(string[] args)
    {
        string? key = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (!int.TryParse(key, out int n)) { Console.Error.WriteLine("用法: diag <设备号>"); return 2; }

        Console.WriteLine("== 诊断 \\\\.\\PhysicalDrive" + n + " ==");
        Console.WriteLine();
        int bad = 0;
        foreach (var line in GuDisk.Core.Devices.DeviceProbe.Run(n))
        {
            if (!line.Ok) bad++;
            string mark = line.Ok ? "OK  " : "FAIL";
            string err = line.Win32Error != 0 ? $"  [Win32 {line.Win32Error}]" : "";
            Console.WriteLine("[" + mark + "] " + line.Step.PadRight(34) + line.Detail + err);
        }
        Console.WriteLine();
        Console.WriteLine(bad == 0 ? "全部通过" : bad + " 项失败");
        return 0;
    }

    // ---------------- list / info ----------------
    private static IReadOnlyList<GuDisk.Core.Devices.PhysicalDriveInfo> Enumerate()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var list = new GuDisk.Core.Devices.DeviceEnumerator().Enumerate();
        Console.Error.WriteLine($"[枚举 {list.Count} 块物理磁盘，耗时 {sw.ElapsedMilliseconds} ms]");
        return list;
    }

    private static int CmdList(string[] args)
    {
        var drives = Enumerate();
        if (drives.Count == 0) { Console.WriteLine("未发现物理磁盘。"); return 1; }

        if (args.Contains("--json"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(drives,
                JsonOpts));
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("序号  设备                总线   协议   容量        盘符     型号 / 固件");
        Console.WriteLine(new string('-', 108));
        foreach (var d in drives)
        {
            Console.WriteLine(string.Format("{0,-4}  {1,-18}  {2,-5} {3,-6} {4,-10} {5,-8} {6}",
                d.DeviceNumber,
                "PhysicalDrive" + d.DeviceNumber,
                d.BusText,
                d.Protocol.ToString(),
                d.CapacityText,
                d.MountPoints.Count > 0 ? string.Join(",", d.MountPoints) : "-",
                d.Model + (d.Firmware.Length > 0 ? " / " + d.Firmware : "")));
            Console.WriteLine(string.Format("      {0,-18}  {1}",
                d.IsSystemDisk ? "系统盘" : (d.IsUsb ? "USB 桥接" : ""),
                string.IsNullOrEmpty(d.BridgeName) ? (d.AdapterName.Length > 0 ? d.AdapterName : "")
                                                   : d.BridgeName + "  (" + d.BridgeEvidence + ")"));
        }
        return 0;
    }

    private static int CmdInfo(string[] args)
    {
        var drives = Enumerate();
        var d = Pick(drives, args);
        if (d is null) return 1;

        if (args.Contains("--json"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(d, JsonOpts));
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("== " + d.Model + " ==");
        Console.WriteLine();
        Row("设备号", d.DeviceNumber.ToString());
        Row("设备路径", d.DevicePath);
        Row("序列号", d.Serial);
        Row("固件", d.Firmware);
        Row("总线", d.BusText);
        Row("协议", d.Protocol.ToString());
        Row("容量", d.CapacityText + "  (" + d.CapacityBytes + " 字节)");
        Row("扇区", d.BytesPerSector + " B");
        Row("可移动", d.IsRemovable ? "是" : "否");
        Row("系统盘", d.IsSystemDisk ? "是" : "否");
        Row("盘符", d.MountPoints.Count > 0 ? string.Join("  ", d.MountPoints) : "（无）");
        Row("控制器", d.AdapterName.Length > 0 ? d.AdapterName : "（未解析）");
        Row("DiskKey", d.DiskKey);
        Console.WriteLine();
        if (d.IsUsb)
        {
            Console.WriteLine("-- USB 桥接 --");
            Row("VID / PID", (d.UsbVendorId is null ? "----" : "0x" + d.UsbVendorId.Value.ToString("X4"))
                           + " / " + (d.UsbProductId is null ? "----" : "0x" + d.UsbProductId.Value.ToString("X4")));
            Row("桥接主控", d.BridgeName ?? "未收录");
            Row("判定依据", d.BridgeEvidence ?? "-");
            Row("建议 -d", d.SmartctlDeviceType ?? "auto");
        }
        Row("设备实例", d.InstanceId);
        return 0;
    }

    private static GuDisk.Core.Devices.PhysicalDriveInfo? Pick(
        IReadOnlyList<GuDisk.Core.Devices.PhysicalDriveInfo> drives, string[] args)
    {
        string? key = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (key is null)
        {
            Console.Error.WriteLine("用法: info <设备号|序列号> [--json]");
            return null;
        }
        var d = drives.FirstOrDefault(x => x.DeviceNumber.ToString() == key)
             ?? drives.FirstOrDefault(x => x.Serial.Contains(key, StringComparison.OrdinalIgnoreCase));
        if (d is null) { Console.Error.WriteLine("未找到匹配的磁盘: " + key); return null; }
        return d;
    }

    /// <summary>耗时显示：不足 1 分钟用秒，否则 hh:mm:ss。</summary>
    private static string FmtDur(TimeSpan t)
        => t.TotalSeconds < 60 ? t.TotalSeconds.ToString("0.0") + " s" : t.ToString(@"hh\:mm\:ss");

    private static void Row(string k, string v)
        => Console.WriteLine("  " + k.PadRight(10) + " " + v);

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static int CmdNotYet(string cmd, string what)
    {
        Console.WriteLine("[未实现] " + cmd + " —— " + what + "将在后续里程碑接入。");
        Console.WriteLine("计划见 docs/实施方案.md 第 7 节（里程碑）。");
        return 0;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine("未知命令: " + cmd + "（用 --help 查看可用命令）");
        return 2;
    }

    // ---------------- policy --selftest ----------------
    private static int CmdSelfTest()
    {
        int fail = 0;
        Console.WriteLine("== " + GuDiskInfo.ProductName + " 环境自检 ==");
        Console.WriteLine();

        Report("运行时", RuntimeInformation.FrameworkDescription, true);

        bool admin;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { admin = false; }
        // 非管理员不是缺陷，是能力受限 —— 记 WARN，不影响退出码
        Report("管理员权限", admin ? "已提升（底层直通可用）" : "受限（SMART / 裸盘扫描 / 写基准将不可用）",
            null, admin ? "OK  " : "WARN");

        bool rootOk;
        try { AppPaths.EnsureCreated(); rootOk = true; } catch { rootOk = false; }
        Report("数据根", AppPaths.Root + (rootOk ? "（可写）" : "（不可写）"), rootOk);
        if (!rootOk) fail++;

        string? pdata = AppPaths.FindProductData();
        bool hasAttr = pdata is not null && File.Exists(Path.Combine(pdata, "smart-attributes.json"));
        Report("产品数据", hasAttr ? pdata! + "（特征库就绪）" : (pdata ?? "(未找到)") + "（特征库待生成：M2）",
            null, hasAttr ? "OK  " : "WARN");

        string? smartDir = AppPaths.FindSmartctlDir();
        if (smartDir is not null)
        {
            Report("smartctl 目录", smartDir, true);
            string smartctl = Path.Combine(smartDir, "smartctl.exe");
            string? ver = RunCapture(smartctl, "--version");
            string first = FirstLine(ver ?? "(无输出)");
            bool verOk = ver is not null && ver.Contains("smartctl", StringComparison.OrdinalIgnoreCase);
            Report("smartctl 引擎", first, verOk, "WARN");
            string drivedb = Path.Combine(smartDir, "drivedb.h");
            bool dbOk = File.Exists(drivedb);
            Report("drivedb", dbOk ? new FileInfo(drivedb).Length + " B" : "缺失", dbOk, "WARN");
        }
        else
        {
            Report("smartctl 引擎", "未找到（发布包需带 tools/smartctl/）", false, "WARN");
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "结论: 通过（无致命项）" : "结论: " + fail + " 项致命问题");
        return fail == 0 ? 0 : 1;
    }

    private static void Report(string name, string value, bool? ok, string? softLevel = null)
    {
        string mark = ok == true ? "OK  " : ok == false ? "FAIL" : (softLevel ?? "OK  ");
        Console.WriteLine("[" + mark + "] " + name.PadRight(14) + " " + value);
    }

    private static string? RunCapture(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return "(超时)"; }
            return outp;
        }
        catch (Exception ex) { return "(执行失败: " + ex.Message + ")"; }
    }

    private static string FirstLine(string s)
    {
        int i = s.IndexOf('\n');
        return (i < 0 ? s : s[..i]).Trim();
    }
}
