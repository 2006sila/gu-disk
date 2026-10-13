using GuDisk.Core;
using GuDisk.Core.Decode;
using GuDisk.Core.Health;

namespace GuDisk.Tests;

/// <summary>
/// 自写断言的控制台测试（零 NuGet）。用法: Gu-disk.Tests [过滤器]
/// 退出码：0 全绿；1 有失败。
/// </summary>
internal static class Program
{
    private static int _passed, _failed;
    private static readonly List<string> Failures = [];

    private static int Main(string[] args)
    {
        string? filter = args.Length > 0 ? args[0] : null;
        Console.WriteLine("== Gu-disk.Tests · " + GuDiskInfo.Version + " ==");

        var tests = new (string Name, Action Body)[]
        {
            ("info.version", InfoVersion),
            ("diskkey.stable", DiskKeyStable),
            ("diskkey.sensitive", DiskKeySensitive),
            ("unit.nvme.dataunit", UnitNvmeDataUnit),
            ("unit.lba", UnitLba),
            ("format.bytes", FormatBytesCases),
            ("nvme.matches-cdi", NvmeMatchesCdi),
            ("format.count", FormatCountCase),
            ("format.poweron", FormatPowerOnTimeCases),
            ("ata.smart-parse", AtaSmartParse),
            ("ata.c5-c6-verdict", AtaC5C6Verdict),
            ("verdict.rules", VerdictRules),
            ("smartresult.fail", SmartResultFail),
            ("history.folder-naming", HistoryFolderNaming),
            ("drivedb.loads", DrivedbLoads),
            ("media.smr-detect", MediaSmrDetect),
            ("media.ssd-detect", MediaSsdDetect),
            ("verdict.normal", VerdictNormal),
            ("db.attribute-names", AttributeDbNames),
            ("bench.runs", BenchRuns),
            ("bench.profiles", BenchProfilesShape),
            ("curve.runs", CurveRuns),
            ("scan.explain", ScanExplain),
            ("scan.grid-downsample", ScanGridDownsample),
            ("scan.grid-exact", ScanGridExact),
            ("history.trend", HistoryTrend),
            ("verdict.growth", VerdictGrowth),
            ("scan.history-diff", ScanHistoryDiff),
            ("ata.errorlog-layout", AtaErrorLogLayout),
            ("nvme.errorlog-layout", NvmeErrorLogLayout),
            ("scan.runs", ScanRuns),
            ("report.formats", ReportFormats),
        };

        foreach (var (name, body) in tests)
        {
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            try { body(); _passed++; Console.WriteLine("  PASS  " + name); }
            catch (Exception ex)
            {
                _failed++;
                Failures.Add(name + ": " + ex.Message);
                Console.WriteLine("  FAIL  " + name + "  -> " + ex.Message);
            }
        }

        Console.WriteLine();
        Console.WriteLine("通过 " + _passed + " / 失败 " + _failed);
        foreach (var f in Failures) Console.WriteLine("  x " + f);
        return _failed == 0 ? 0 : 1;
    }


    // ---------------- M2 新增 ----------------

    /// <summary>历史目录名必须与 CrystalDiskInfo 的 Smart/&lt;型号&gt;&lt;序列号&gt;/ 逐字一致，否则导入会错位。</summary>
    private static void HistoryFolderNaming()
    {
        Eq("Predator SSD GM7 2TBSN2400000000000A",
           GuDisk.Core.History.HistoryStore.FolderFor("Predator SSD GM7 2TB", "SN2400000000000A"), "Predator");
        Eq("YMTC YMSS2ED08D25MCYM2400000000000B",
           GuDisk.Core.History.HistoryStore.FolderFor("YMTC YMSS2ED08D25MC", "YM2400000000000B"), "YMTC");
    }

    private static void DrivedbLoads()
    {
        var db = GuDisk.Core.Decode.DrivedbIndex.Load();
        True(db is not null, "应能加载 drivedb-index.json（先跑 tools/convert-drivedb.mjs）");
        True(db!.Total > 500, "条目数应 > 500，实际 " + db.Total);
        True(db.SmrCount >= 5, "SMR 标注条目应 ≥ 5，实际 " + db.SmrCount);
    }

    private static void MediaSmrDetect()
    {
        var db = GuDisk.Core.Decode.DrivedbIndex.Load();
        var v = GuDisk.Core.Health.CmrSmrDetector.Detect("ST2000DM008-2FR102", null, null, null, db);
        Eq(GuDisk.Core.Health.MediaTechnology.HddSmr, v.Technology, "希捷 ST2000DM008 应判为 SMR");
        True(v.ConfidencePercent >= 90, "有官方标注时置信度应 ≥ 90");
        True(v.Reason.Contains("drivedb"), "依据里应写明来源");
    }

    private static void MediaSsdDetect()
    {
        var nvme = FakeNvme();
        var v = GuDisk.Core.Health.CmrSmrDetector.Detect("Any NVMe", null, nvme, null, null);
        Eq(GuDisk.Core.Health.MediaTechnology.Ssd, v.Technology, "NVMe 必为 SSD");
        Eq(100, v.ConfidencePercent, "NVMe 判定置信度");
    }

    private static void AttributeDbNames()
    {
        var db = GuDisk.Core.Decode.SmartAttributeDb.Load();
        True(db is not null, "应能加载 smart-attributes.json（先跑 tools/convert-lang.mjs）");
        Eq("重新分配扇区数", db!.Resolve(0x05, "Some HDD", false).Name, "通用表 05");
        Eq("严重警告标志", db.Resolve(0x01, "any", true).Name, "NVMe 表 01");
        var kioxia = db.Resolve(0xA9, "KIOXIA EXCERIA PLUS G3", false);
        Eq("坏块计数", kioxia.Name, "Kioxia 厂商节覆盖 0xA9");
        Eq("Kioxia", kioxia.VendorSection, "命中的厂商节");
        True(db.VendorCount >= 40, "厂商节应 ≥ 40，实际 " + db.VendorCount);
    }

    private static GuDisk.Core.Smart.NvmeSmartLog FakeNvme(ulong mediaErrors = 0, byte percentageUsed = 0,
        ulong unsafeShutdowns = 0, short tempC = 40)
        => new(CriticalWarning: 0, CompositeTemperatureK: (short)(tempC + 273),
               AvailableSpare: 100, AvailableSpareThreshold: 10, PercentageUsed: percentageUsed,
               DataUnitsRead: 0, DataUnitsWritten: 0, HostReadCommands: 0, HostWriteCommands: 0,
               ControllerBusyTime: 0, PowerCycles: 0, PowerOnHours: 100, UnsafeShutdowns: unsafeShutdowns,
               MediaErrors: mediaErrors, ErrorLogEntries: 0,
               WarningTempTimeMinutes: 0, CriticalTempTimeMinutes: 0, TemperatureSensors: [tempC],
               ThermalTransition1: 0, ThermalTransition2: 0, ThermalTime1Minutes: 0, ThermalTime2Minutes: 0,
               Raw512: new byte[512]);

    /// <summary>真跑一遍测速引擎（32 MiB，快速档），验证 Direct I/O 管线端到端可用。</summary>
    private static void BenchRuns()
    {
        string path = Path.Combine(GuDisk.Core.App.AppPaths.Root, "bench_selftest.tmp");
        var res = GuDisk.Core.Benchmark.BenchRunner.Run(
            path, 32L * 1024 * 1024, GuDisk.Core.Benchmark.BenchProfiles.Quick, "zero", 1, false, false);

        True(res.Success, "测速应成功：" + res.Error);
        Eq(2, res.Results.Count, "快速档行数");
        foreach (var row in res.Results)
        {
            True(row.Success, row.Label + " 应成功：" + row.Error);
            True(row.MibPerSecond > 1, row.Label + " 吞吐量应 > 1 MB/s，实际 " + row.MibPerSecond.ToString("N1"));
            True(row.Iops > 1, row.Label + " IOPS 应 > 1");
        }
        True(!File.Exists(path), "测试文件应被自动清理");
    }

    private static void BenchProfilesShape()
    {
        Eq(4, GuDisk.Core.Benchmark.BenchProfiles.Cdm4.Rows.Count, "CDM 档行数");
        Eq(13, GuDisk.Core.Benchmark.BenchProfiles.Atto.Rows.Count, "ATTO 阶梯档数");
        Eq(7, GuDisk.Core.Benchmark.BenchProfiles.AccessTime.Rows.Count, "随机访问档数");
        var atto = GuDisk.Core.Benchmark.BenchProfiles.Atto.Rows;
        Eq(4096u, atto[0].BlockSize, "ATTO 首档");
        Eq(16777216u, atto[^1].BlockSize, "ATTO 末档");
        True(atto.All(r => r.QueueDepth == 4), "ATTO 队列应统一为 4");
    }

    /// <summary>真跑一遍全盘曲线（128 MiB / 4 段），验证采样与判读可用。</summary>
    private static void CurveRuns()
    {
        string path = Path.Combine(GuDisk.Core.App.AppPaths.Root, "curve_selftest.tmp");
        var res = GuDisk.Core.Benchmark.FullDiskCurve.RunFile(path, 128L * 1024 * 1024, samples: 4);
        True(res.Success, "曲线应成功：" + res.Error);
        True(res.Points.Count >= 4, "采样点应 >= 4，实际 " + res.Points.Count);
        True(res.AverageMibPerSecond > 1, "平均吞吐应 > 1 MB/s");
        True(res.Diagnosis.Length > 0, "应给出判读文本");
        True(!File.Exists(path), "曲线测试文件应被自动清理");
    }

    private static void ScanExplain()
    {
        True(GuDisk.Core.Scan.SurfaceScanner.Explain(23).Contains("CRC"), "23 应解释为 CRC");
        True(GuDisk.Core.Scan.SurfaceScanner.Explain(27).Contains("坏"), "27 应解释为坏扇区");
        True(GuDisk.Core.Scan.SurfaceScanner.Explain(5).Contains("管理员"), "5 应提示权限");
        True(GuDisk.Core.Scan.SurfaceScanner.Explain(12345).Contains("未知"), "未知码要兜底");
    }

    /// <summary>
    /// 热力图降采样：50 万个桶（约 488 GB）必须被压到 ≤ 1536 格，
    /// 而**计数仍按真实桶数** —— 这是"扫 4 TB 机械盘不会卡死"的保证。
    /// </summary>
    private static void ScanGridDownsample()
    {
        const int n = 500_000;
        var map = new List<GuDisk.Core.Scan.ScanBucket>(n);
        for (int i = 0; i < n; i++)
        {
            bool bad = i % 1000 == 0;
            map.Add(new GuDisk.Core.Scan.ScanBucket(i * 1048576L, 1048576,
                bad ? GuDisk.Core.Scan.BlockClass.Error : GuDisk.Core.Scan.BlockClass.Ok,
                bad ? 0 : 200));
        }

        var (cells, counts) = GuDisk.Core.Scan.HeatMapGrid.Build(map,
            b => b.Worst == GuDisk.Core.Scan.BlockClass.Error ? 6 : 1, 7);

        True(cells.Count <= GuDisk.Core.Scan.HeatMapGrid.MaxCells,
            "格数必须降采样到 ≤1536，实际 " + cells.Count);
        True(cells.Count * 1000 >= n, "降采样不能过度丢信息，实际 " + cells.Count + " 格");
        True(counts[6] == n / 1000, "坏块计数应仍是真实桶数 " + (n / 1000) + "，实际 " + counts[6]);
        True(counts[1] == n - n / 1000, "正常桶计数 " + counts[1]);
        True(cells.Any(c => c.Level == 6), "含坏块的格必须显示最差等级");
    }

    /// <summary>桶数不超上限时不能降采样 —— 1 GB 扫出来必须还是 1024 格一一对应。</summary>
    private static void ScanGridExact()
    {
        var map = Enumerable.Range(0, 1024)
            .Select(i => new GuDisk.Core.Scan.ScanBucket(i * 1048576L, 1048576,
                GuDisk.Core.Scan.BlockClass.Ok, 100 + i)).ToList();
        var (cells, counts) = GuDisk.Core.Scan.HeatMapGrid.Build(map, _ => 2, 7);
        True(cells.Count == 1024, "未超上限时格数应等于桶数，实际 " + cells.Count);
        True(cells.All(c => c.BucketCount == 1), "未降采样时每格应只含 1 个桶");
        True(counts[2] == 1024, "计数 " + counts[2]);
        True(cells[0].StartBytes == 0 && cells[^1].EndBytes == 1024L * 1048576,
            "首尾偏移应覆盖整个扫描范围");
    }

    /// <summary>真扫一遍 64 MiB 测试文件，验证扫描管线、热力图分桶与 CSV 导出。</summary>
    private static void ScanRuns()
    {
        string path = Path.Combine(GuDisk.Core.App.AppPaths.Root, "scan_selftest.tmp");
        var res = GuDisk.Core.Scan.SurfaceScanner.RunFile(path, 64L * 1024 * 1024);

        True(res.Success, "扫描应成功：" + res.Error);
        Eq(64, res.ScannedBlocks, "1 MiB 块下 64 MiB 应有 64 块");
        Eq(0, res.ErrorCount, "测试文件不该有坏块");
        True(res.Map.Count == res.ScannedBlocks, "热力图桶数应等于块数");
        True(res.AverageMibPerSecond > 1, "平均速度应 > 1 MB/s");
        True(res.Conclusion.Length > 0, "应给出结论");
        True(GuDisk.Core.Scan.SurfaceScanner.ToCsv(res).StartsWith("offset_bytes"), "CSV 表头");
        True(!File.Exists(path), "扫描文件应被清理");
    }

    /// <summary>三种报告格式都要能生成，且关键内容必须在。</summary>
    private static void ReportFormats()
    {
        var nvme = FakeNvme(mediaErrors: 3, percentageUsed: 42, unsafeShutdowns: 25, tempC: 47);
        var drive = new GuDisk.Core.Devices.PhysicalDriveInfo(
            DeviceNumber: 0, DevicePath: "d", InterfacePath: "i", InstanceId: "x",
            Model: "Test SSD GM7", Serial: "SN0001", Firmware: "FW9",
            Bus: GuDisk.Core.Devices.BusKind.Nvme, Protocol: GuDisk.Core.Devices.StorageProtocolKind.Nvme,
            AdapterName: "PCI", UsbVendorId: null, UsbProductId: null,
            Bridge: GuDisk.Core.Devices.UsbBridgeKind.None, BridgeName: null, BridgeEvidence: null,
            SmartctlDeviceType: "nvme", CapacityBytes: 1_000_000_000_000, BytesPerSector: 512,
            IsRemovable: false, IsSystemDisk: false, MountPoints: new List<string> { "C:" });

        var attrs = GuDisk.Core.Smart.NvmeSmartReader.ToAttributes(nvme);
        var smart = new GuDisk.Core.Smart.SmartReadOutcome(true, "Native", null, nvme, null, null,
            attrs, "NVMe Health Log", [], []);
        var media = GuDisk.Core.Health.CmrSmrDetector.Detect("Test SSD GM7", null, nvme, null, null);
        var health = GuDisk.Core.Health.StatusVerdictEvaluator.Evaluate(Nvme(nvme));
        var diag = new GuDisk.Core.App.DriveDiagnostic(drive, smart, media, health, "db", "drivedb", []);

        // 带上温度历史 —— 报告里的 SVG 迷你折线就是靠它渲染的
        var hist = Enumerable.Range(0, 24)
            .Select(i => (DateTime.Now.AddHours(i - 24), 38.0 + (i % 6)))
            .ToList();
        var input = new GuDisk.Core.Report.ReportInput(diag, null, null, null, hist);

        string txt = GuDisk.Core.Report.ReportWriter.ToText(input);
        True(txt.Contains("Test SSD GM7") && txt.Contains("状态判定") && txt.Contains("建议"), "TXT 关键内容");

        string json = GuDisk.Core.Report.ReportWriter.ToJson(input);
        True(json.Contains("\"verdict\"") && json.Contains("SN0001"), "JSON 关键字段");

        string html = GuDisk.Core.Report.ReportWriter.ToHtml(input);
        True(html.StartsWith("<!DOCTYPE html>"), "HTML 头");
        True(html.Contains("Test SSD GM7") && html.Contains("介质类型判定"), "HTML 关键区块");
        True(html.Contains("<svg"), "有温度历史时 HTML 应内嵌 SVG 图表");
        True(html.Contains("温度历史"), "应含温度历史区块");
        True(html.Length > 2000, "HTML 不应是空壳，实际 " + html.Length + " 字符");
        True(health.Findings.Count > 0, "构造的盘应判出异常项");
    }

    /// <summary>
    /// 用**合成数据**验证 ATA SMART 解析：手工拼一个 512 字节的 SMART READ DATA 块。
    /// 本机没有 SATA 盘，这是唯一能把解析层钉死的手段 —— 采集层仍需真机验证。
    /// 属性项布局：[0]ID [1-2]状态 [3]当前 [4]最差 [5-10]原始值(小端) [11]保留。
    /// </summary>
    private static (byte[] Data, byte[] Thr) FakeAtaSmart()
    {
        var data = new byte[512];
        var thr = new byte[512];
        var rows = new (byte Id, byte Cur, byte Worst, ulong Raw)[]
        {
            (0x05, 100, 100, 0),          // 重映射扇区：0
            (0xC5, 100, 100, 3),          // 待处理扇区：3
            (0xC6, 100, 100, 2),          // 无法纠正：2
            (0x09, 90, 90, 12345),        // 通电时间
            (0xC2, 62, 40, 0x2D1926),     // 温度：当前 0x26=38，最低 0x19=25，最高 0x2D=45
        };
        for (int i = 0; i < rows.Length; i++)
        {
            int off = 2 + i * 12;
            var r = rows[i];
            data[off] = r.Id;
            data[off + 1] = 0; data[off + 2] = 0;
            data[off + 3] = r.Cur;
            data[off + 4] = r.Worst;
            for (int b = 0; b < 6; b++) data[off + 5 + b] = (byte)((r.Raw >> (8 * b)) & 0xFF);
            thr[off] = r.Id;               // 阈值块
            thr[off + 1] = 0;
        }
        return (data, thr);
    }

    private static void AtaSmartParse()
    {
        var (data, thr) = FakeAtaSmart();
        var res = GuDisk.Core.Smart.AtaSmartReader.Build(data, thr, null, (true, true, "0"), "test");
        True(res.Success, "解析应成功：" + res.Error);
        True(res.Smart is not null, "应有 SMART 块");

        var attrs = res.Smart!.Attributes;
        Eq(5, attrs.Count, "应解析出 5 条属性");

        var c5 = attrs.First(a => a.Id == 0xC5);
        Eq(100, c5.Current, "C5 当前值");
        Eq(100, c5.Worst, "C5 最差值");
        Eq(3UL, c5.Raw, "C5 原始值应为 3（原始字节在 off+5..off+10）");
        Eq("000000000003", c5.RawHex, "C5 十六进制");
        True(c5.NameCn.Contains("待"), "C5 中文名，实际：" + c5.NameCn);

        var c6 = attrs.First(a => a.Id == 0xC6);
        Eq(2UL, c6.Raw, "C6 原始值应为 2");

        Eq(12345UL, attrs.First(a => a.Id == 0x09).Raw, "通电时间原始值");
        Eq(0x2D1926UL, attrs.First(a => a.Id == 0xC2).Raw, "温度原始值");

        // 关键回归：原始值绝不能把 [0] 属性 ID 算进去
        True(attrs.All(a => a.Raw < 0x1000000 || a.Id != 0xC5), "回归：C5 原始值不应被 ID 字节污染");
        Eq("0 个扇区", attrs.First(a => a.Id == 0x05).RawMeaning, "重映射 0 的文本");
        Eq("38 ℃（当前）/ 25 ℃（最低）/ 45 ℃（最高）", attrs.First(a => a.Id == 0xC2).RawMeaning, "温度文本");
    }

    /// <summary>C5/C6 非零必须判到"危急"，且依据能指明扇区数。</summary>
    private static void AtaC5C6Verdict()
    {
        var (data, thr) = FakeAtaSmart();
        var res = GuDisk.Core.Smart.AtaSmartReader.Build(data, thr, null, (true, true, "0"), "test");
        var outcome = new GuDisk.Core.Smart.SmartReadOutcome(true, "Native", null, null, res.Smart,
            res.Identity, res.Smart!.Attributes, "ATA SMART", [], []);
        var v = GuDisk.Core.Health.StatusVerdictEvaluator.Evaluate(outcome);

        Eq(GuDisk.Core.Health.DriveState.Critical, v.State, "C5/C6 非零应判危急");
        True(v.Findings.Any(f => f.Source.Contains("0xC5")), "应指出 C5");
        True(v.Findings.Any(f => f.Source.Contains("0xC6")), "应指出 C6");
        True(v.Findings.Any(f => f.RawValue.Contains("3") && f.Source.Contains("0xC5")), "C5 依据应带扇区数");
        True(v.Checks.Any(c => c.Source.Contains("0x05")), "重映射为 0 应出现在已核对项里");
    }

    /// <summary>
    /// 历史趋势：**样本不足必须返回 null（不猜）**，窗口外的点不能算进来。
    /// 这是 B1"变化速率"判定的地基 —— 地基错了上面全错。
    /// </summary>
    private static void HistoryTrend()
    {
        // **必须用工作区内的路径**：沙箱只放行工作区，用 %TEMP% 会写失败，
        // 而 Append 按设计吞掉异常 → 表现为"历史是空的"，很容易误判成逻辑 bug（踩过）。
        string root = Path.Combine(GuDisk.Core.App.AppPaths.Root,
            "hist-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var h = new GuDisk.Core.History.HistoryStore(root);
            True(h.TrendOf("m", "s", "Reallocated", 30) is null, "没历史应返回 null");

            h.Append("m", "s", "Reallocated", DateTime.Now.AddDays(-10), 0);
            True(h.TrendOf("m", "s", "Reallocated", 30) is null, "只有一个点应返回 null（不猜）");

            h.Append("m", "s", "Reallocated", DateTime.Now, 7);
            var tr = h.TrendOf("m", "s", "Reallocated", 30);
            if (tr is null || !tr.Grew)
            {
                var dump = string.Join(" | ", Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetFileName(f) + "=[" + string.Join("/", File.ReadAllLines(f)) + "]"));
                throw new Exception("应从 0 涨到 7；实际 tr="
                    + (tr is null ? "<null>" : tr.Points + " 点 " + tr.First + "→" + tr.Last + " Δ=" + tr.Delta)
                    + "；文件：" + dump);
            }
            True(Math.Abs(tr!.Delta - 7) < 0.001, "增量应为 +7，实际 " + tr.Delta);
            True(h.FirstPositiveAt("m", "s", "Reallocated") is not null, "应能找到首次正值时间");

            // 40 天前的点不该进 30 天窗口
            h.Append("m", "s", "Reallocated", DateTime.Now.AddDays(-40), 99);
            var tr2 = h.TrendOf("m", "s", "Reallocated", 30);
            True(tr2 is not null && tr2.Delta < 50, "窗口外的点不该被算进来，实际 " + tr2!.Delta);

            // 从未有过正值的指标 → FirstPositiveAt 应为 null
            h.Append("m", "s", "Pending", DateTime.Now, 0);
            True(h.FirstPositiveAt("m", "s", "Pending") is null, "全是 0 时不该报首次正值");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>
    /// 增长判定：**没有历史时不能凭空报"增长"**；有历史且真在涨必须报出来，并带增量数字。
    /// </summary>
    private static void VerdictGrowth()
    {
        var (data, thr) = FakeAtaSmart();
        var res = GuDisk.Core.Smart.AtaSmartReader.Build(data, thr, null, (true, true, "0"), "test");
        var outcome = new GuDisk.Core.Smart.SmartReadOutcome(true, "Native", null, null, res.Smart,
            res.Identity, res.Smart!.Attributes, "ATA SMART", [], []);

        var none = GuDisk.Core.Health.StatusVerdictEvaluator.Evaluate(outcome);
        True(!none.Findings.Any(f => f.Source.Contains("增长")), "没有历史时不该报增长");

        var fake = new GuDisk.Core.History.HistoryStore.Trend(
            "Reallocated", 5, DateTime.Now.AddDays(-20), DateTime.Now, 0, 12);
        var v = GuDisk.Core.Health.StatusVerdictEvaluator.Evaluate(outcome, 55, 70, 30000,
            m => m == "Reallocated" ? fake : null, _ => null);

        True(v.Findings.Any(f => f.Source.Contains("增长") && f.Source.Contains("0x05")),
            "从 0 涨到 12 应报出增长");
        True(v.Findings.Any(f => f.Source.Contains("增长") && f.RawValue.Contains("+12")),
            "增长条目应带增量数字");
        True(v.Findings.Any(f => f.Source.Contains("增长") && f.Level == GuDisk.Core.Health.DriveState.Warning),
            "增长应判为警告级");

        // 没涨（持平）不该报
        var flat = new GuDisk.Core.History.HistoryStore.Trend(
            "Reallocated", 5, DateTime.Now.AddDays(-20), DateTime.Now, 12, 12);
        var v2 = GuDisk.Core.Health.StatusVerdictEvaluator.Evaluate(outcome, 55, 70, 30000,
            m => m == "Reallocated" ? flat : null, _ => null);
        True(!v2.Findings.Any(f => f.Source.Contains("增长")), "持平不该报增长");
    }

    /// <summary>
    /// 扫描跨次对比：**方向不能搞反**。造两个偏移集合验证新增/减少各自算对，
    /// 尤其是"上次有、这次没有"必须算成**减少**而不是新增。
    /// </summary>
    private static void ScanHistoryDiff()
    {
        GuDisk.Core.Scan.ScanBlock B(long off, string cls) => new(0, off, 1048576,
            cls == "E" ? GuDisk.Core.Scan.BlockClass.Error
                       : cls == "S" ? GuDisk.Core.Scan.BlockClass.Slow
                                    : GuDisk.Core.Scan.BlockClass.Ok,
            0, 0, "x");

        GuDisk.Core.Scan.ScanResult Mk(params GuDisk.Core.Scan.ScanBlock[] bs)
            => new(true, null, "file", 1 << 30, 1048576, TimeSpan.Zero, 1 << 30, 1024, 100, 1, 200,
                bs, [], "结论");

        var prev = new GuDisk.Core.Scan.ScanHistory.Snapshot(
            "k", DateTime.Now.AddDays(-3), "file", 1 << 30, 2, 1,
            [100L, 200L], [300L], false);

        // 上次 {100,200} 坏 + {300} 慢 → 这次 {200,400} 坏 + {300,500} 慢
        var cur = Mk(B(100, "O"), B(200, "E"), B(400, "E"), B(300, "S"), B(500, "S"));
        var d = GuDisk.Core.Scan.ScanHistory.Compare(prev, cur);

        Eq(1, d.NewErrors, "新增坏块应只有 400");
        Eq(1, d.GoneErrors, "100 消失应算减少，不是新增");
        Eq(1, d.NewSlow, "新增偏慢应只有 500");
        Eq(0, d.GoneSlow, "300 仍在，不该算减少");
        True(!d.Same, "有变化时 Same 应为 false");
        True(d.Text.Contains("新增坏块 1") && d.Text.Contains("减少坏块 1"), "文案要分别说清");

        True(!GuDisk.Core.Scan.ScanHistory.Compare(null, cur).HasPrevious, "没有上次快照时应标记首次");
        // 真正的"完全一致"：坏块 {100,200}、慢块 {300} 都要在
        True(GuDisk.Core.Scan.ScanHistory.Compare(prev, Mk(B(100, "E"), B(200, "E"), B(300, "S"))).Same,
            "完全一致时应判为无变化");
    }

    /// <summary>
    /// ATA 错误日志的字节布局（ACS-4）。**本机没有 SATA 盘，这段只能靠这个测试兜住。**
    ///
    /// 曾经整体错位 2 字节：错误码取 [0]（那是 Error 寄存器）、LBA 取 [3..6]（应为 [5..7]）。
    /// 测试最后特意把**旧算法**也算一遍，确认它给出不同结果 —— 否则这就是个测不出回归的假测试。
    /// </summary>
    private static void AtaErrorLogLayout()
    {
        // 布局：[0] 日志版本　[1] 条目数　[2 + i*90] 第 i 条
        // 注意 byte 1 同时被"条目数"占用 —— 正因为早期夹具把条目 0 放到了 0，
        // 这个撞车才暴露出"条目其实从字节 2 开始"。
        byte[] data = new byte[512];
        data[0] = 0x01;              // 日志版本
        data[1] = 2;                 // 条目数
        int o = 2;                   // 第 0 条从字节 2 开始
        data[o + 0] = 0x40;          // Error 寄存器，bit6 = UNC
        data[o + 1] = 0x51;          // Status
        data[o + 2] = 0x34;          // 扩展错误码 LSB
        data[o + 3] = 0x12;          // 扩展错误码 MSB  → 0x1234
        data[o + 4] = 0x08;          // 扇区数
        data[o + 5] = 0xBC;          // LBA low
        data[o + 6] = 0x0A;          // LBA mid
        data[o + 7] = 0x00;          // LBA high        → 0x000ABC
        data[o + 8] = 0xE0;          // Device
        data[o + 9] = 0x25;          // 命令
        int o2 = 2 + 90;             // 第 1 条
        data[o2 + 1] = 0x50;
        data[o2 + 2] = 0xFF; data[o2 + 3] = 0x00;
        data[o2 + 4] = 0x01;
        data[o2 + 5] = 0x11; data[o2 + 6] = 0x22; data[o2 + 7] = 0x33;   // LBA = 0x332211

        var list = GuDisk.Core.Smart.SelfTest.ParseAtaErrorLog(data);
        Eq(2, list.Count, "条目数应取 [1]");
        Eq(0x1234L, list[0].ErrorCode, "错误码应取条目内 [2-3]，不是 [0]");
        Eq(0x000ABCL, list[0].Lba, "LBA 应取 [5-7]");
        True(list[0].Uncorrectable, "Error 寄存器 bit6 置位应判为不可纠正");
        Eq(0x332211L, list[1].Lba, "第二条 LBA 也应取 [5-7]");
        True(!list[1].Uncorrectable, "第二条 bit6 未置位");

        long oldWrong = data[o + 3] | (data[o + 4] << 8) | (data[o + 5] << 16) | ((long)data[o + 6] << 24);
        True(oldWrong != list[0].Lba, "旧错位算法必须给出不同结果，否则测不出回归（实际 " + oldWrong + "）");
    }

    /// <summary>
    /// NVMe 错误日志布局。曾经把 Uncorrectable 写成取"错误计数的高字节"的 bit0 ——
    /// 那与错误性质毫无关系。正解看 Status Field 的 SCT（bit 9-11），SCT=2 才是介质/数据完整性错误。
    /// </summary>
    private static void NvmeErrorLogLayout()
    {
        byte[] log = new byte[4096];
        int o = 0;
        log[o + 0] = 3; log[o + 1] = 0;                       // 错误计数 = 3
        ushort sf = (ushort)((2 << 9) | (0x81 << 1));         // SCT=2, SC=0x81
        log[o + 6] = (byte)(sf & 0xFF); log[o + 7] = (byte)(sf >> 8);
        BitConverter.GetBytes(0x1122334455UL).CopyTo(log, o + 8);

        var list = GuDisk.Core.Smart.SelfTest.ParseNvmeErrorLog(log);
        Eq(1, list.Count, "应解析出 1 条");
        Eq(3L, list[0].ErrorCode, "错误计数取 [0-1]");
        Eq(0x1122334455L, list[0].Lba, "LBA 取 [8-15]");
        True(list[0].Uncorrectable, "SCT=2 应判为介质/数据完整性错误");

        // 回归：错误计数高字节为奇数 —— 旧代码会据此误报"不可纠正"
        byte[] log2 = new byte[4096];
        log2[0] = 1; log2[1] = 0x01;                          // 计数 = 0x0101，高字节 = 1
        var l2 = GuDisk.Core.Smart.SelfTest.ParseNvmeErrorLog(log2);
        Eq(1, l2.Count, "非空条目应被解析");
        True(!l2[0].Uncorrectable, "错误计数的高字节与错误性质无关，不该判成不可纠正");

        // 空日志不该产出条目
        Eq(0, GuDisk.Core.Smart.SelfTest.ParseNvmeErrorLog(new byte[4096]).Count, "全零日志应无条目");
    }

    private static void Eq<T>(T expect, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expect, actual))
            throw new Exception(what + " 期望 [" + expect + "] 实际 [" + actual + "]");
    }

    private static void True(bool cond, string what)
    {
        if (!cond) throw new Exception(what);
    }

    private static void InfoVersion()
        => True(System.Text.RegularExpressions.Regex.IsMatch(GuDiskInfo.Version, @"^\d+\.\d+\.\d+$"), "版本号应形如 x.y.z");

    private static void DiskKeyStable()
    {
        var a = DiskKey.From("Predator SSD GM7 2TB", "SN2400000000000A");
        var b = DiskKey.From("predator ssd gm7 2tb", "sn2400000000000a");   // 小写变体，与上一行同盘
        Eq(a, b, "同盘不同大小写");
        Eq(16, a.Length, "DiskKey 长度");
    }

    private static void DiskKeySensitive()
    {
        var a = DiskKey.From("Predator SSD GM7 2TB", "SN2400000000000A");
        var b = DiskKey.From("Predator SSD GM7 2TB", "SN2400000000000B");
        True(a != b, "换序列号必须换 Key");
        Eq("UNKNOWN", DiskKey.From(null, null), "空输入");
    }

    private static void UnitNvmeDataUnit() => Eq(512_000d, UnitConverter.NvmeDataUnitsToBytes(1), "1 data unit");

    private static void UnitLba() => Eq(1024d, UnitConverter.LbaToBytes(2), "2 LBA");

    private static void FormatBytesCases()
    {
        // 1024 进制（与 Windows / CrystalDiskInfo 一致）
        Eq("512 B", UnitConverter.FormatBytes(512), "字节档不带小数");
        Eq("4 B", UnitConverter.FormatBytes(4), "极小值");
        Eq("1.0 KB", UnitConverter.FormatBytes(1024), "1 KiB");
        Eq("1.5 MB", UnitConverter.FormatBytes(1024 * 1024 * 1.5), "1.5 MiB");
        Eq("1.0 TB", UnitConverter.FormatBytes(1024d * 1024 * 1024 * 1024), "1 TiB");
    }

    /// <summary>对标 CDI：Predator GM7 的 HostReads=12028(GB)、HostWrites=5510(GB) 是实测值。</summary>
    private static void NvmeMatchesCdi()
    {
        double read = UnitConverter.NvmeDataUnitsToBytes(25_225_120);
        double write = UnitConverter.NvmeDataUnitsToBytes(11_556_964);
        Eq("12 028 GB", UnitConverter.FormatGigabytes(read), "读取量对齐 CDI");
        Eq("5 511 GB", UnitConverter.FormatGigabytes(write), "写入量对齐 CDI");
    }

    private static void FormatCountCase()
        => Eq("23 507 343 456", UnitConverter.FormatCount(23_507_343_456L), "空格千分位");

    private static void FormatPowerOnTimeCases()
    {
        Eq("207 小时", UnitConverter.FormatHours(207), "精确小时");
        Eq("8 天 15 小时", UnitConverter.FormatPowerOnTime(207), "人读-天");
        Eq("10 天 0 小时", UnitConverter.FormatPowerOnTime(240), "人读-整十天");
        Eq("1 年 0 天 0 小时", UnitConverter.FormatPowerOnTime(8760), "人读-年");
    }

    /// <summary>判定规则：每条异常都必须能指回一个具体属性，级别要正确。</summary>
    private static void VerdictRules()
    {
        var bad = FakeNvme(mediaErrors: 5, percentageUsed: 96, unsafeShutdowns: 0, tempC: 40);
        var v = GuDisk.Core.Health.StatusVerdictEvaluator.Evaluate(Nvme(bad));
        Eq(GuDisk.Core.Health.DriveState.Critical, v.State, "介质错误 + 寿命 96% 应判危急");
        True(v.Findings.Any(f => f.Source.Contains("0x0E")), "应指出介质错误属性");
        True(v.Findings.All(f => f.RawValue.Length > 0), "每条依据都要有原始值");
        True(v.Findings.All(f => f.Reading.Length > 0), "每条依据都要有判读");
        True(v.Findings.All(f => f.Advice is not null), "异常项都要给建议");

        var warm = FakeNvme(tempC: 60);
        var v2 = GuDisk.Core.Health.StatusVerdictEvaluator.Evaluate(Nvme(warm));
        Eq(GuDisk.Core.Health.DriveState.Attention, v2.State, "60 ℃ 应判注意");

        var shutdowns = FakeNvme(unsafeShutdowns: 793);
        Eq(GuDisk.Core.Health.DriveState.Attention,
            GuDisk.Core.Health.StatusVerdictEvaluator.Evaluate(Nvme(shutdowns)).State,
            "793 次不安全关机属使用习惯，只判注意");

        var full = FakeNvme(percentageUsed: 85);
        Eq(GuDisk.Core.Health.DriveState.Warning,
            GuDisk.Core.Health.StatusVerdictEvaluator.Evaluate(Nvme(full)).State, "寿命 85% 应判警告");
    }

    private static void VerdictNormal()
    {
        var v = GuDisk.Core.Health.StatusVerdictEvaluator.Evaluate(Nvme(FakeNvme()));
        Eq(GuDisk.Core.Health.DriveState.Normal, v.State, "干净盘应判正常");
        Eq(0, v.Findings.Count, "不应有异常项");
        True(v.Checks.Count >= 8, "应列出已核对项，实际 " + v.Checks.Count);
        Eq("正常", v.Label, "档位名");

        var fail = new GuDisk.Core.Smart.SmartReadOutcome(false, "none", "读不到", null, null, null, [], "", [], []);
        var v2 = GuDisk.Core.Health.StatusVerdictEvaluator.Evaluate(fail);
        Eq(GuDisk.Core.Health.DriveState.Attention, v2.State, "读不到 SMART 应判注意");
        True(!v2.SmartAvailable, "应标记 SMART 不可用");
    }

    private static GuDisk.Core.Smart.SmartReadOutcome Nvme(GuDisk.Core.Smart.NvmeSmartLog log)
        => new(true, "Native", null, log, null, null,
               GuDisk.Core.Smart.NvmeSmartReader.ToAttributes(log), "NVMe Health Log", [], []);

    private static void SmartResultFail()
    {
        var r = GuDisk.Core.Smart.SmartReadResult.Fail("Native", "ERROR_ACCESS_DENIED", "需要管理员");
        True(!r.Success, "Fail 结果 Success 应为 false");
        Eq(0, r.Attempts.Count, "Attempts 应为空列表而非 null");
        Eq("Native", r.Engine, "引擎名");
    }
}
