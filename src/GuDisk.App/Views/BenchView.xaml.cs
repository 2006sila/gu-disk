using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Diagnostics;
using GuDisk.Core.Benchmark;
using GuDisk.Core.Decode;
using GuDisk.Core.Devices;

namespace GuDisk.App.Views;

public partial class BenchView : UserControl
{
    /// <summary>
    /// 结果矩阵的一行（照 CDM：一行一个档位，右侧两列读数）。
    /// W1/W2 是数值左边那条长度条的像素宽，按全局最大值等比算出来 —— 四档之间的差距一眼可见。
    /// </summary>
    public sealed record MatrixRow(string Label, string V1, string V2, double W1, double W2,
        Brush Brush1, Brush Brush2, Brush Bar1, Brush Bar2);

    private static readonly uint[] Blocks = [4096u, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576];

    private PhysicalDriveInfo? _drive;
    private CancellationTokenSource? _cts;
    private BenchResult? _last;
    private readonly List<double> _samples = [];
    private double _cpuSum;
    private int _cpuCount;

    public BenchView() => InitializeComponent();

    public void Show(PhysicalDriveInfo drive, string status)
    {
        _drive = drive;
        TargetText.Text = "目标　" + TargetDirectory()
            + "　·　会在该目录下建一个测试文件，跑完自动删除";
    }

    private string TargetDirectory()
    {
        string custom = PathBox.Text.Trim();
        string dir = custom.Length > 0
            ? custom
            : (_drive?.MountPoints.FirstOrDefault() ?? "C:");
        // 一律补结尾分隔符：用户填目录时不会带，而引擎靠它区分"目录"和"文件路径"
        char sep = System.IO.Path.DirectorySeparatorChar;
        return dir.EndsWith(sep) || dir.EndsWith('\\') ? dir : dir + sep;
    }

    private static long ParseSize(string? text)
    {
        text ??= "1G";
        long mult = text.EndsWith("M", StringComparison.OrdinalIgnoreCase) ? 1024L * 1024 : 1024L * 1024 * 1024;
        return long.TryParse(text[..^1], out var n) ? n * mult : 1024L * 1024 * 1024;
    }

    private long CurrentSize() => ParseSize((SizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString());

    private int CurrentRuns()
        => int.TryParse((RunsBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var v) ? v : 5;

    private int CurrentMix() => MixBox.SelectedIndex switch { 1 => 10, 2 => 30, 3 => 50, 4 => 70, _ => -1 };

    private int CurrentDuration()
        => int.TryParse(DurationBox.Text, out var s) && s > 0 ? Math.Clamp(s, 1, 600) : 0;

    private BenchProfile CurrentProfile()
    {
        if (ProfileBox.SelectedIndex == 4)
        {
            uint bs = Blocks[Math.Clamp(BlockBox.SelectedIndex, 0, Blocks.Length - 1)];
            int qd = int.TryParse(QdBox.Text, out var q) ? Math.Clamp(q, 1, 128) : 8;
            int th = int.TryParse(ThreadBox.Text, out var t) ? Math.Clamp(t, 1, 64) : 1;
            return BenchProfiles.Custom(bs, qd, th, RandomBox.IsChecked == true, CurrentMix());
        }

        var profile = ProfileBox.SelectedIndex switch
        {
            1 => BenchProfiles.Atto,
            2 => BenchProfiles.AccessTime,
            3 => BenchProfiles.Quick,
            _ => BenchProfiles.Cdm4
        };
        int mix = CurrentMix();
        return mix >= 0 ? profile with { WritePercent = mix } : profile;
    }

    private async void OnRun(object sender, RoutedEventArgs e) => await RunAsync(write: true);
    private async void OnRunReadOnly(object sender, RoutedEventArgs e) => await RunAsync(write: false);

    private async Task RunAsync(bool write)
    {
        if (_drive is null || _cts is not null) return;

        var profile = CurrentProfile();
        long size = CurrentSize();
        int runs = CurrentRuns();
        int duration = CurrentDuration();
        string dir = TargetDirectory();   // 界面控件只在 UI 线程读

        RunBtn.IsEnabled = false; ReadOnlyBtn.IsEnabled = false; CancelBtn.IsEnabled = true;
        MatrixList.ItemsSource = null; SummaryText.Text = ""; StatusText.Text = "";
        Bar.Value = 0; _samples.Clear(); Wave.Points = []; WaveFill.Points = []; WaveInfo.Text = "按 I/O 量";
        PhaseText.Text = write ? "正在测速（读 + 写）…" : "正在测速（只读）…";
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // CPU 占用：跟着同一条采样回调走。用"本进程处理器时间 ÷ 墙上时间 ÷ 核数"算，
        // 与 CDM/HDTune 报的是同一个量（进程自己占了多少，不含别的东西抢 IO）。
        var proc = Process.GetCurrentProcess();
        var cpuClock = Stopwatch.StartNew();
        TimeSpan cpuStart = proc.TotalProcessorTime;
        int cores = Math.Max(1, Environment.ProcessorCount);
        _cpuSum = 0; _cpuCount = 0;

        // 采样回调在计时器线程上触发 —— 更新界面必须回 UI 线程
        Action<LiveSample> onSample = s => Dispatcher.BeginInvoke(() =>
        {
            _samples.Add(s.MibPerSecond);
            if (_samples.Count > 900) _samples.RemoveAt(0);
            try
            {
                double wall = cpuClock.Elapsed.TotalMilliseconds;
                if (wall > 200)
                {
                    double pct = Math.Min(100, (proc.TotalProcessorTime - cpuStart).TotalMilliseconds
                                               / wall / cores * 100);
                    _cpuSum += pct; _cpuCount++;
                    CpuText.Text = "CPU " + pct.ToString("0.0") + "%";
                }
            }
            catch { }
            RedrawWave();
        });

        try
        {
            var res = await Task.Run(() => BenchRunner.Run(dir, size, profile, "random", runs, write,
                keepFile: false, log: m => Dispatcher.Invoke(() => PhaseText.Text = m), ct: token,
                durationSeconds: duration, sampleIntervalMs: 200, onSample: onSample), token);

            _last = res;
            if (!res.Success) { StatusText.Text = "测速失败：" + res.Error; return; }

            RenderMatrix(res);
            Bar.Value = 100;
            PhaseText.Text = "完成";
            SummaryText.Text = "测试文件 " + UnitConverter.FormatBytes(res.FileSize) + "　·　每档 " + res.Runs
                + " 次取最好　·　" + (duration > 0 ? "每档跑 " + duration + " 秒" : "按 I/O 量跑完")
                + "　·　总耗时 " + res.TotalSeconds.ToString("0.0") + " 秒"
                + (_cpuCount > 0 ? "　·　平均 CPU " + (_cpuSum / _cpuCount).ToString("0.0") + "%" : "");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已中止（已完成的档位保留在上表）。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "测速异常：" + ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            RunBtn.IsEnabled = true; ReadOnlyBtn.IsEnabled = true; CancelBtn.IsEnabled = false;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnUnitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (_last is { Success: true } r) RenderMatrix(r);
    }

    private static string Fmt(BenchRowResult r, int unit) => unit switch
    {
        1 => r.Iops.ToString("N0"),
        2 => r.LatencyMs.ToString("N3"),
        _ => r.MibPerSecond.ToString("N1"),
    };

    /// <summary>把逐档结果铺成 CDM 那样的矩阵：一行一个档位，右侧读/写两列。</summary>
    private void RenderMatrix(BenchResult res)
    {
        int unit = UnitBox.SelectedIndex;
        string suffix = unit switch { 1 => "IOPS", 2 => "ms", _ => "MB/s" };
        var lime = (Brush)FindResource("Brush.Lime");
        var dim = (Brush)FindResource("Brush.Text3");
        var bar = (Brush)FindResource("Brush.LimeDim");
        var noBar = Brushes.Transparent;

        var groups = res.Results.GroupBy(r => r.Label).ToList();

        // 条的基准取全局最大值（读/写两列共用），这样两列之间也能横向比较
        double Metric(BenchRowResult r) => unit switch { 1 => r.Iops, 2 => r.LatencyMs, _ => r.MibPerSecond };
        double max = groups.SelectMany(g => g).Where(r => r.Success).Select(Metric)
                           .DefaultIfEmpty(0).Max();
        // 延迟是"越小越好"，用长度条表达会反过来误导 → 这一档不画条
        double Bar(double v) => unit == 2 || max <= 0 ? 0 : Math.Max(4, 104 * v / max);

        var rows = new List<MatrixRow>();
        foreach (var g in groups)
        {
            var read = g.FirstOrDefault(r => r.Mode == "read");
            var wr = g.FirstOrDefault(r => r.Mode == "write");
            var mix = g.FirstOrDefault(r => r.Mode.StartsWith("mix", StringComparison.Ordinal));

            if (mix is not null)
            {
                // 混合模式没有读/写之分，整行只出一个数
                Col1Header.Text = mix.Mode + "（" + suffix + "）";
                Col2Header.Text = "";
                bool ok = mix.Success;
                rows.Add(new MatrixRow(g.Key, ok ? Fmt(mix, unit) : "—", "",
                    ok ? Bar(Metric(mix)) : 0, 0, ok ? lime : dim, lime, bar, noBar));
            }
            else
            {
                Col1Header.Text = "读取（" + suffix + "）";
                Col2Header.Text = "写入（" + suffix + "）";
                bool ok1 = read?.Success == true, ok2 = wr?.Success == true;
                rows.Add(new MatrixRow(g.Key,
                    ok1 ? Fmt(read!, unit) : "—",
                    ok2 ? Fmt(wr!, unit) : "—",
                    ok1 ? Bar(Metric(read!)) : 0,
                    ok2 ? Bar(Metric(wr!)) : 0,
                    ok1 ? lime : dim, ok2 ? lime : dim,
                    ok1 ? bar : noBar, ok2 ? bar : noBar));
            }
        }
        MatrixList.ItemsSource = rows;
    }

    /// <summary>
    /// 把采样点画成折线。
    /// **先折叠连续空转**：档位之间、建文件期间、结束之后速率都是 0，
    /// 原样画出来就是一条占满宽度的平线，真正有 I/O 的那几秒被压成左边一小撮。
    /// 连续的空转点只保留一个，这样波形会被拉满，可读性完全不一样。
    /// </summary>
    private void RedrawWave()
    {
        double w = WaveCanvas.ActualWidth, h = WaveCanvas.ActualHeight;
        if (w <= 2 || h <= 2) return;

        var pts = new List<double>(_samples.Count);
        bool prevIdle = false;
        foreach (var v in _samples)
        {
            bool idle = v < 1;
            if (idle && prevIdle) continue;
            pts.Add(v);
            prevIdle = idle;
        }
        if (pts.Count < 2) return;

        double max = Math.Max(1, pts.Max());
        var line = new PointCollection();
        for (int i = 0; i < pts.Count; i++)
        {
            double x = w * i / (pts.Count - 1);
            double y = h - 3 - (h - 8) * (pts[i] / max);
            line.Add(new Point(x, y));
        }
        Wave.Points = line;
        WaveFill.Points = new PointCollection(line) { new Point(w, h), new Point(0, h) };

        int idleCount = _samples.Count - pts.Count;
        WaveInfo.Text = "峰值 " + max.ToString("N0") + " MB/s · " + pts.Count + " 个有效采样点"
            + (idleCount > 0 ? "（已折叠 " + idleCount + " 个空转点）" : "");
    }
}
