using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GuDisk.Core.Decode;
using GuDisk.Core.Devices;
using GuDisk.Core.Scan;

namespace GuDisk.App.Views;

public partial class ScanView : UserControl
{
    public sealed record HeatCell(Brush Brush, string Tip);
    public sealed record LevelRow(string Name, string Range, Brush Color, string Count);
    public sealed record BlockRow(string OffsetText, string ClassText, string SpeedText, string ErrorText, string Explanation);

    /// <summary>
    /// 七档响应等级（照参考软件的"优秀 / 良好 / 正常 / 一般 / 欠佳 / 严重 / 损坏"）。
    ///
    /// 判据是**每块（1 MiB）的耗时**而不是吞吐量 —— 耗时对机械盘和固态是同一把尺子：
    /// 1 MiB 走 X MB/s 就是 1000/X 毫秒，所以"< 3 ms"等价于"> 333 MB/s"，
    /// 现代固态落在优秀/良好，机械盘落在良好/正常，一眼能看出是不是同一类介质。
    ///
    /// 颜色是一套语义色阶（深绿 → 红），不随深浅主题变；中间几档是这里定义的，
    /// 两端的绿/琥珀/红与主题令牌同值，保证和界面其它地方一致。
    /// </summary>
    private static readonly (string Name, double MaxMs, Color C)[] Levels =
    [
        ("优秀", 3,          Color.FromRgb(0x35, 0xD0, 0x7F)),
        ("良好", 10,         Color.FromRgb(0x7B, 0xD9, 0x4B)),
        ("正常", 50,         Color.FromRgb(0xC6, 0xF2, 0x4E)),
        ("一般", 150,        Color.FromRgb(0xE8, 0xD4, 0x4D)),
        ("欠佳", 500,        Color.FromRgb(0xE8, 0xB3, 0x41)),
        ("严重", -1,         Color.FromRgb(0xE8, 0x79, 0x2B)),   // 阈值 = 用户设的超时
        ("损坏", double.MaxValue, Color.FromRgb(0xE5, 0x48, 0x4D)),
    ];

    private static readonly Brush[] LevelBrushes = Levels
        .Select(l => { var b = new SolidColorBrush(l.C); b.Freeze(); return (Brush)b; }).ToArray();

    // 阻止系统睡眠：机械盘全盘扫描动辄几小时，中途进睡眠会让盘停转，
    // 之后每一块都要等盘唤醒（几秒）→ 七档全变橙红，结果全是假的。
    // 只请求"系统保持唤醒"，不请求显示器常亮（长时间扫描没必要亮屏）。
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    /// <summary>下拉里的固定档位（GB）；索引 8/9/10 是动态档，单独算。</summary>
    private static readonly long[] FixedGb = [1, 10, 100, 500, 1000, 2000, 4000, 8000];

    private PhysicalDriveInfo? _drive;
    private CancellationTokenSource? _cts;
    private ScanResult? _last;

    public ScanView()
    {
        InitializeComponent();
        RenderLegend(new int[Levels.Length], 3000);
    }

    public void Show(PhysicalDriveInfo drive, string status)
    {
        _drive = drive;
        RefreshSizeOptions();
        if (_last is null)
            TargetText.Text = "目标　" + TargetDirectory()
                + "　·　只在自建临时文件范围内读，不碰你的数据，跑完自动删除";
    }

    /// <summary>卷的剩余空间（拿不到就返回 0）。</summary>
    private long FreeSpaceBytes()
    {
        try
        {
            string? mp = _drive?.MountPoints.FirstOrDefault();
            if (mp is null) return 0;
            var di = new DriveInfo(mp);
            return di.IsReady ? di.AvailableFreeSpace : 0;
        }
        catch { return 0; }
    }

    private static string Gb(long bytes) => UnitConverter.FormatBytes((ulong)Math.Max(0, bytes));

    /// <summary>
    /// 按当前盘刷新下拉：超出可扫范围的固定档灰掉，两个动态档标上实际数值。
    /// 不同容量的盘（500 GB ~ 18 TB）用同一套档位，所以必须实时告诉用户"上限是多少"。
    /// </summary>
    private void RefreshSizeOptions()
    {
        if (_drive is null || SpaceText is null) return;
        long free = FreeSpaceBytes();
        long cap = (long)_drive.CapacityBytes;
        bool raw = ModeBox.SelectedIndex == 1;

        for (int i = 0; i < FixedGb.Length; i++)
        {
            if (SizeBox.Items[i] is not ComboBoxItem item) continue;
            long bytes = FixedGb[i] * 1024L * 1024 * 1024;
            item.Content = (FixedGb[i] >= 1000 ? (FixedGb[i] / 1000) + " TB" : FixedGb[i] + " GB");
            // 文件模式上限 = 剩余空间；裸盘模式整盘可读，固定档都有效
            item.IsEnabled = raw || bytes <= free;
        }
        if (SizeBox.Items[8] is ComboBoxItem half)
        {
            half.Content = "一半空闲空间（约 " + Gb(free / 2) + "）";
            half.IsEnabled = free > 0;
        }
        if (SizeBox.Items[9] is ComboBoxItem all)
        {
            all.Content = "全部空闲空间（约 " + Gb(free * 92 / 100) + "）";
            all.IsEnabled = free > 0;
        }
        if (SizeBox.Items[10] is ComboBoxItem whole)
        {
            whole.Content = "整盘（" + Gb(cap) + "，仅裸盘模式）";
            whole.IsEnabled = raw;
        }

        // 选中的那项如果被禁用了，自动退到"全部空闲空间"
        if (SizeBox.SelectedItem is ComboBoxItem sel && !sel.IsEnabled)
            SizeBox.SelectedIndex = free > 0 ? 9 : 0;

        SpaceText.Text = raw
            ? "裸盘只读扫描：整个盘 " + Gb(cap) + "（只发读命令，不写任何数据）"
            : "可扫描上限：" + Gb(free) + " 空闲（卷共 " + Gb(cap) + "）"
              + "　·　留 8% 余量给系统，所以「全部空闲」按 " + Gb(free * 92 / 100) + " 扫";
        UpdateTargetHint();
    }

    /// <summary>把"实际会扫多少"提前写出来，免得建文件失败才发现选大了。</summary>
    private void UpdateTargetHint()
    {
        long want = CurrentSize();
        if (TargetText is null) return;
        TargetText.Text = "目标　" + TargetDirectory()
            + "　·　本次将扫描 " + Gb(want);
    }

    private void OnSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateTargetHint();
    }

    /// <summary>把剩余时间写成"几小时几分" —— 机械盘全盘扫要几小时，
    /// 显示 05:20:00 这种计时器格式远不如"约 5 小时 20 分"直观。</summary>
    private static string Human(TimeSpan t) => t.TotalHours >= 1
        ? "约 " + (int)t.TotalHours + " 小时 " + t.Minutes + " 分"
        : t.TotalMinutes >= 1 ? "约 " + (int)t.TotalMinutes + " 分 " + t.Seconds + " 秒"
        : "约 " + Math.Max(1, (int)t.TotalSeconds) + " 秒";

    private int TimeoutMs()
        => int.TryParse(TimeoutBox.Text, out var v) ? Math.Clamp(v, 50, 60_000) : 3000;

    private string TargetDirectory()
    {
        string custom = PathBox.Text.Trim();
        string dir = custom.Length > 0 ? custom : (_drive?.MountPoints.FirstOrDefault() ?? "C:");
        char sep = System.IO.Path.DirectorySeparatorChar;
        return dir.EndsWith(sep) || dir.EndsWith('/') ? dir : dir + sep;
    }

    /// <summary>
    /// 本次实际要扫的字节数。
    /// **文件模式一律按剩余空间封顶**（留 8%），并且裸盘模式忽略下拉、直接整盘 ——
    /// 以前选超过剩余空间的档位会去建一个装不下的文件，白等一场才报错。
    /// </summary>
    private long CurrentSize()
    {
        if (_drive is null) return 1024L * 1024 * 1024;
        long free = FreeSpaceBytes();
        long cap = (long)_drive.CapacityBytes;
        bool raw = ModeBox.SelectedIndex == 1;
        if (raw) return cap;

        int i = Math.Max(0, SizeBox.SelectedIndex);
        long want = i switch
        {
            >= 0 and <= 7 => FixedGb[i] * 1024L * 1024 * 1024,
            8 => free / 2,
            9 => free * 92 / 100,
            _ => cap,
        };
        long ceiling = Math.Max(64L * 1024 * 1024, free * 92 / 100);
        return Math.Min(want, Math.Min(ceiling, cap));
    }

    /// <summary>每块耗时 → 档位。耗时 = 1000 / (MiB/s)，块大小固定 1 MiB。</summary>
    private static int Classify(ScanBucket b, int timeoutMs)
    {
        if (b.Worst is BlockClass.Error or BlockClass.Timeout) return 6;
        double ms = b.MibPerSecond > 0.01 ? 1000.0 / b.MibPerSecond : double.MaxValue;
        for (int i = 0; i < 5; i++) if (ms < Levels[i].MaxMs) return i;
        return ms < timeoutMs ? 5 : 6;
    }

    private static string RangeText(int level, int timeoutMs) => level switch
    {
        0 => "< 3 ms",
        1 => "< 10 ms",
        2 => "< 50 ms",
        3 => "< 150 ms",
        4 => "< 500 ms",
        5 => "500 – " + timeoutMs + " ms",
        _ => "≥ 超时 / 读错误",
    };

    private void RenderLegend(int[] counts, int timeoutMs)
    {
        LegendList.ItemsSource = Levels.Select((l, i) =>
            new LevelRow(l.Name, RangeText(i, timeoutMs), LevelBrushes[i],
                counts[i].ToString("N0"))).ToList();
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        if (_drive is null || _cts is not null) return;
        bool raw = ModeBox.SelectedIndex == 1;
        if (raw && MessageBox.Show(
                "裸盘扫描需要管理员权限，将对整盘做只读扫描（不写任何数据）。\n盘越大耗时越长。确认继续？",
                "Gu-disk", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        int timeout = TimeoutMs();
        long size = CurrentSize();
        string dir = TargetDirectory();   // 控件只在 UI 线程读

        RunBtn.IsEnabled = false; StopBtn.IsEnabled = true; CsvBtn.IsEnabled = false; ResetBtn.IsEnabled = false;
        ProblemList.ItemsSource = null;
        HeatMap.ItemsSource = null;
        RenderLegend(new int[Levels.Length], timeout);
        MinText.Text = AvgText.Text = MaxText.Text = "—";
        Bar.Value = 0;
        PhaseText.Text = raw ? "正在只读扫描整盘…" : "正在建测试文件并扫描…";
        ConclusionText.Text = "";
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        if (SleepGuardBox.IsChecked == true)
            try { SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED); } catch { }

        var progress = new Progress<ScanProgress>(p =>
        {
            Bar.Value = p.Percent;
            PhaseText.Text = string.Format("{0:0.0}%   已扫 {1}   速度 {2:N0} MB/s",
                p.Percent, UnitConverter.FormatBytes(p.DoneBytes), p.MibPerSecond);
            EtaText.Text = p.Eta is null ? "" : "剩余 " + Human(p.Eta.Value);
        });

        try
        {
            var res = await Task.Run(() => raw
                ? SurfaceScanner.RunRaw(_drive.DeviceNumber, progress: progress, ct: token, timeoutMs: timeout)
                : SurfaceScanner.RunFile(System.IO.Path.Combine(dir,
                    "Gu-disk_scan_" + Guid.NewGuid().ToString("N")[..8] + ".tmp"),
                    size, progress: progress, ct: token, timeoutMs: timeout));

            if (!res.Success) { ConclusionText.Text = "扫描失败：" + res.Error; return; }

            _last = res;
            CsvBtn.IsEnabled = true;
            RenderResult(res, timeout);

            // 跨次对比：**参考软件里没人做好这件事**（扫完即弃）。
            // 对一块用了三年的盘，"这次 12 个坏块"远不如"上次 0 个、这次 12 个"有信息量。
            try
            {
                var prev = ScanHistory.Load(_drive.DiskKey);
                var diff = ScanHistory.Compare(prev, res);
                ConclusionText.Text = res.Conclusion + "　·　" + diff.Text;
                ScanHistory.Save(ScanHistory.From(_drive.DiskKey, res));
            }
            catch { /* 对比失败不影响扫描结果展示 */ }
        }
        catch (OperationCanceledException) { ConclusionText.Text = "已中止（已扫部分保留在上图）。"; }
        catch (Exception ex) { ConclusionText.Text = "扫描异常：" + ex.Message; }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            try { SetThreadExecutionState(ES_CONTINUOUS); } catch { }   // 无论成败都要放开，否则一直不睡
            RunBtn.IsEnabled = true; StopBtn.IsEnabled = false; ResetBtn.IsEnabled = true;
        }
    }

    private void RenderResult(ScanResult res, int timeout)
    {
        // ---- 地图 + 分级计数 ----
        // 降采样到固定格数：扫 1 GB 是 1024 格，扫 4 TB 也还是 ≤1536 格（否则 WPF 会卡死）。
        // 计数仍按真实桶数走，所以"损坏 N"永远等于 N 个 1 MiB 块。
        var (grid, counts) = HeatMapGrid.Build(res.Map, b => Classify(b, timeout), Levels.Length);
        HeatMap.ItemsSource = grid.Select(c => new HeatCell(LevelBrushes[c.Level],
            UnitConverter.FormatBytes(c.StartBytes)
            + (c.BucketCount > 1 ? " – " + UnitConverter.FormatBytes(c.EndBytes) : "")
            + "　" + Levels[c.Level].Name + "　" + c.WorstMibPerSecond.ToString("N0") + " MB/s")).ToList();
        RenderLegend(counts, timeout);
        int merged = grid.Count > 0 ? grid[0].BucketCount : 1;
        MapInfo.Text = res.Map.Count + " 个 " + UnitConverter.FormatBytes(res.BlockSize) + " 块"
            + (merged > 1 ? "　·　显示为 " + grid.Count + " 格（每格 " + merged + " 块，取最差等级）"
                          : "　·　显示为 " + grid.Count + " 格")
            + "　·　已扫 " + UnitConverter.FormatBytes(res.ScannedBytes)
            + "　·　耗时 " + res.Elapsed.TotalSeconds.ToString("0.0") + " 秒";

        // ---- 摘要三块（HDTune 那三格）----
        MinText.Text = res.MinMibPerSecond.ToString("N0") + " MB/s";
        AvgText.Text = res.AverageMibPerSecond.ToString("N0") + " MB/s";
        MaxText.Text = res.MaxMibPerSecond.ToString("N0") + " MB/s";
        Bar.Value = 100;
        PhaseText.Text = "完成";
        ConclusionText.Text = res.Conclusion;

        // ---- 明细表 ----
        ProblemList.ItemsSource = res.ProblemBlocks.Take(2000).Select(b => new BlockRow(
            UnitConverter.FormatBytes(b.OffsetBytes),
            b.Class switch { BlockClass.Error => "损坏", BlockClass.Timeout => "超时", _ => "偏慢" },
            b.MibPerSecond > 0 ? b.MibPerSecond.ToString("N0") + " MB/s" : "—",
            b.Win32Error == 0 ? "—" : b.Win32Error.ToString(),
            b.Explanation)).ToList();
        ProblemHeader.Text = "问题块（" + res.ProblemBlocks.Count + " 个：损坏 "
            + res.ErrorCount + " · 偏慢 " + res.SlowCount + "）";
    }

    private void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _last = null;
        HeatMap.ItemsSource = null;
        ProblemList.ItemsSource = null;
        ProblemHeader.Text = "问题块（尚未扫描）";
        RenderLegend(new int[Levels.Length], TimeoutMs());
        MapInfo.Text = "尚未扫描";
        MinText.Text = AvgText.Text = MaxText.Text = "—";
        ConclusionText.Text = "选择范围后点「开始扫描」。";
        Bar.Value = 0;
        PhaseText.Text = "";
        EtaText.Text = "";
        CsvBtn.IsEnabled = false;
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_last is not { } res) return;
        try
        {
            App_Core_Paths_Ensure();
            string file = System.IO.Path.Combine(GuDisk.Core.App.AppPaths.Root,
                "scan-" + _drive?.DiskKey + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
            var sb = new System.Text.StringBuilder("偏移,类型,速度MB/s,错误码,解释\n");
            foreach (var b in res.ProblemBlocks)
                sb.Append(b.OffsetBytes).Append(',').Append(b.Class).Append(',')
                  .Append(b.MibPerSecond.ToString("0.0")).Append(',').Append(b.Win32Error).Append(',')
                  .Append('"').Append(b.Explanation.Replace("\"", "\"\"")).Append('"').Append('\n');
            File_WriteAllText(file, sb.ToString());
            ConclusionText.Text = "已导出：" + file;
        }
        catch (Exception ex) { ConclusionText.Text = "导出失败：" + ex.Message; }
    }

    // 两个转发：避免为了一句调用在文件头加一堆 using
    private static void App_Core_Paths_Ensure() => GuDisk.Core.App.AppPaths.EnsureCreated();
    private static void File_WriteAllText(string path, string text)
        // **这里的 BOM 是有意留的**：CSV 表头是中文，Excel 靠 BOM 才能认出 UTF-8，
        // 否则中文会变成乱码。其它地方（历史 CSV / scan-history.json）都写不带 BOM 的，
        // 因为那些是我们的程序自己解析，BOM 只会给解析添麻烦。
        => System.IO.File.WriteAllText(path, text, System.Text.Encoding.UTF8);
}
