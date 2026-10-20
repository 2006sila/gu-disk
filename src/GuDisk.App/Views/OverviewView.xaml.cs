using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GuDisk.App.ViewModels;
using GuDisk.Core.App;
using GuDisk.Core.Decode;
using GuDisk.Core.Health;
using GuDisk.Core.History;
using GuDisk.Core.Smart;

namespace GuDisk.App.Views;

public partial class OverviewView : UserControl
{
    public sealed record KvRow(string Key, string Value);
    public sealed record ChipRow(string Key, string Value, Brush Dot);
    public sealed record JumpRow(string Title, string Desc, string Tag);

    /// <summary>请求切到别的页签（宿主窗口显式订阅）。</summary>
    public event Action<string>? NavigateRequested;

    private readonly HistoryStore _history = new();
    private MainViewModel? _lastVm;

    public OverviewView()
    {
        InitializeComponent();
        // 首次 Show() 时画布还没布局，ActualWidth 是 0 —— 布局完成后再补画一次
        TempHistCanvas.SizeChanged += (_, _) => { if (_lastVm is not null) RenderTempHistory(_lastVm); };
    }

    private static readonly IReadOnlyList<(string Title, string Desc, string Tag)> Jumps =
    [
        ("S.M.A.R.T. 明细", "逐条属性、原始值与解读", "smart"),
        ("自检与工具", "短/扩展自检、自检日志、AAM/APM", "selftest"),
        ("基准测速", "CDM 四档 / ATTO 阶梯 / 混合读写", "bench"),
        ("坏道扫描", "表面扫描 + 热力地图", "scan"),
        ("诊断报告", "导出 HTML / TXT / JSON", "report"),
    ];

    public void Show(MainViewModel vm)
    {
        var d = vm.Diagnostic;
        var drive = vm.SelectedDrive;
        if (d is null || drive is null) return;

        _lastVm = vm;
        RenderTempHistory(vm);

        var v = d.Verdict;
        var findings = v.Findings;

        // ---------- 头部 ----------
        ModelText.Text = drive.Model;
        CapacityChip.Text = drive.CapacityText;
        BusChip.Text = drive.BusText + " / " + drive.Protocol;
        MediaChip.Text = d.Media.Label;

        var gb = (Brush)FindResource(StatusVerdictEvaluator.ColorTokenOf(v.State));
        StateText.Text = v.Label;
        StateText.Foreground = gb;
        StateBadgeText.Text = v.SmartAvailable ? v.Label : "SMART 不可读";
        StateBadgeText.Foreground = gb;
        StateBadgeBorder.Background = (Brush)FindResource(v.State switch
        {
            DriveState.Normal => "Brush.OkDim",
            DriveState.Attention => "Brush.WarnDim",
            DriveState.Warning => "Brush.WarnDim",
            _ => "Brush.BadDim",
        });
        CountsText.Text = v.CountsText + (findings.Count == 0 ? "" : "\n最严重：" + findings[0].Source);

        // ---------- 温度（独立刷新那一路） ----------
        TempText.Text = vm.TemperatureText;
        // 副行：更新时刻 + 刷新周期 + **多传感器**（NVMe 通常有 2~8 个，复合温度是它们的加权值，
        // 看最高那个才知道热点在哪）
        string sensors = "";
        var n = d.Smart.Nvme;   // NVMe 健康日志（ATA 盘为 null）
        if (n?.TemperatureSensors is { Count: > 1 } ts)
        {
            var valid = ts.Where(k => k > 0).Select(k => k - 273.15).ToList();
            if (valid.Count > 0)
                sensors = "　·　传感器 " + valid.Count + " 个，最高 " + valid.Max().ToString("0.#") + " ℃";
        }
        TempAtText.Text = (vm.TemperatureAt is { } at
            ? "更新于 " + at.ToString("HH:mm:ss") + "　·　每 " + AppSettings.Current.TempRefreshSeconds + " 秒刷新"
            : "等待首次读取") + sensors;
        if (vm.TemperatureCelsius is { } t)
            TempText.Foreground = (Brush)FindResource(t >= AppSettings.Current.TempCritC ? "Brush.Bad"
                : t >= AppSettings.Current.TempWarnC ? "Brush.Warn" : "Brush.Lime");

        // ---------- 规格网格 ----------
        var id = d.Smart.Identity;
        bool nvme = d.Smart.Nvme is not null;
        var spec = new List<KvRow>
        {
            new("固件", drive.Firmware),
            new("序列号", drive.Serial),
            new("接口", drive.BusText),
            new("传输模式", id?.TransferMode ?? (nvme ? "PCIe（NVMe）" : "-")),
            new("盘符", drive.MountPoints.Count > 0 ? string.Join(" ", drive.MountPoints) : "（无）"),
            new("标准", id?.Standard ?? (nvme ? "NVMe" : "-")),
            new("主机读取", n is null ? "-" : UnitConverter.FormatBytes(UnitConverter.NvmeDataUnitsToBytes(n.DataUnitsRead))),
            new("主机写入", n is null ? "-" : UnitConverter.FormatBytes(UnitConverter.NvmeDataUnitsToBytes(n.DataUnitsWritten))),
            new("通电次数", n is not null ? UnitConverter.FormatCount((long)n.PowerCycles) + " 次"
                : id is { PowerOnCount: > 0 } ? UnitConverter.FormatCount(id.PowerOnCount) + " 次" : "-"),
            new("通电时间", n is not null ? UnitConverter.FormatCount((long)n.PowerOnHours) + " 小时"
                : id is { PowerOnHours: > 0 } ? UnitConverter.FormatCount(id.PowerOnHours) + " 小时" : "-"),
            new("介质转速", MediaSpeedText(id, d.Media)),
            // TRIM 对 ATA 是 DATA SET MANAGEMENT，对 NVMe 是 Dataset Management(Deallocate)，
            // 两者能力位在不同地方。以前对 NVMe 直接写死"支持" —— 那是编的。
            // 现在：ATA 报实读值；NVMe 明说"未读能力位"（诚实 > 好看）。
            // TRIM 对 ATA 是 DATA SET MANAGEMENT，对 NVMe 是 Dataset Management(Deallocate)。
            // 以前对 NVMe 直接写死"支持" —— 那是编的；现在读 ONCS 的真实能力位。
            new("TRIM", drive.NvmeTrim is { } nt ? (nt ? "支持（DSM）" : "不支持（固件未声明）")
                        : id is not null ? (id.TrimSupported ? "支持" : "不支持") : "-"),
            // 2026-10-13 补：NVMe 健康日志里解析了但从没用上的四组数据
            new("温度累计", n is null ? "-"
                : "警告 " + n.WarningTempTimeMinutes + " 分 · 临界 " + n.CriticalTempTimeMinutes + " 分"),
            new("温控降频", n is null ? "-"
                : (n.ThermalTransition1 + n.ThermalTransition2) + " 次 / "
                  + (n.ThermalTime1Minutes + n.ThermalTime2Minutes) + " 分钟"),
            new("主机命令", n is null ? "-"
                : "读 " + UnitConverter.FormatCount((long)n.HostReadCommands)
                  + " · 写 " + UnitConverter.FormatCount((long)n.HostWriteCommands)),
            new("平均写入", AverageWriteSize(n)),
        };
        SpecGrid.ItemsSource = spec;

        // ---------- 关键属性条 ----------
        string Val(string contains, string fallback = "—")
            => v.Checks.FirstOrDefault(c => c.Source.Contains(contains))?.RawValue
            ?? findings.FirstOrDefault(f => f.Source.Contains(contains))?.RawValue ?? fallback;
        DriveState Worst(params string[] keys)
            => findings.Where(f => keys.Any(k => f.Source.Contains(k)))
                       .Select(f => f.Level).DefaultIfEmpty(DriveState.Normal).Max();

        Brush Dot(DriveState s) => (Brush)FindResource(StatusVerdictEvaluator.ColorTokenOf(s));

        var ribbon = new List<ChipRow>();
        if (nvme)
        {
            ribbon.Add(new("0x0E 介质错误", Val("0x0E"), Dot(Worst("0x0E"))));
            ribbon.Add(new("0x0D 不安全关机", Val("0x0D"), Dot(Worst("0x0D"))));
            ribbon.Add(new("0x05 已用寿命", Val("0x05"), Dot(Worst("0x05"))));
            ribbon.Add(new("0x03 可用备用", Val("0x03"), Dot(Worst("0x03"))));
            ribbon.Add(new("0x02 综合温度", Val("0x02"), Dot(Worst("0x02"))));
        }
        else
        {
            ribbon.Add(new("0x05 重新分配扇区", Val("0x05"), Dot(Worst("0x05"))));
            ribbon.Add(new("0xC5 待映射扇区", Val("0xC5"), Dot(Worst("0xC5"))));
            ribbon.Add(new("0xC6 无法纠正", Val("0xC6"), Dot(Worst("0xC6"))));
            ribbon.Add(new("0xC7 接口 CRC", Val("0xC7"), Dot(Worst("0xC7"))));
            ribbon.Add(new("0x09 通电时间", Val("0x09"), Dot(Worst("0x09"))));
        }
        Ribbon.ItemsSource = ribbon;

        // ---------- 四张分析卡 ----------
        void Fill(ItemsControl list, TextBlock badge, string[] keys, (string Key, string Value)[] rows)
        {
            list.ItemsSource = rows.Select(r => new KvRow(r.Key, r.Value)).ToList();
            var worst = Worst(keys);
            badge.Text = StatusVerdictEvaluator.LabelOf(worst);
            badge.Foreground = Dot(worst);
        }

        Fill(Card1, Card1Badge, ["0x05", "0xC5", "0xC6", "0x0E", "0xA9", "介质"],
        [
            ("介质类型", d.Media.Label + "（置信度 " + d.Media.ConfidencePercent + "%）"),
            (nvme ? "介质错误" : "重映射扇区", Val(nvme ? "0x0E" : "0x05")),
            (nvme ? "可用备用空间" : "待处理扇区", Val(nvme ? "0x03" : "0xC5")),
            (nvme ? "已用寿命" : "无法纠正错误", Val(nvme ? "0x05" : "0xC6")),
        ]);

        Fill(Card2, Card2Badge, ["0xC7", "交叉校验", "0xBB"],
        [
            ("传输模式", spec[3].Value),
            ("接口速率", nvme ? "PCIe（NVMe）" : (id?.TransferMode ?? "-")),
            ("交叉校验", d.CrossCheck.Count == 0 ? "未执行"
                : d.CrossCheck.Count + " 项" + (d.Disagreements.Count == 0 ? "，全部一致" : "，" + d.Disagreements.Count + " 项存疑")),
            (nvme ? "错误日志条目" : "接口 CRC 错误", Val(nvme ? "0x0F" : "0xC7")),
        ]);

        Fill(Card3, Card3Badge, ["0x0A", "0x0C", "读写"],
        [
            ("主机读取", spec[6].Value),
            ("主机写入", spec[7].Value),
            ("通电时间", spec[9].Value),
            (nvme ? "控制器忙时长" : "累计通电次数", Val(nvme ? "0x0A" : "0x0C")),
        ]);

        Card4Badge.Text = findings.Count == 0 ? "无异常" : findings.Count + " 项";
        Card4Badge.Foreground = gb;
        Card4.ItemsSource = findings.Count == 0
            ? [new KvRow("已核对", v.Checks.Count + " 项全部正常")]
            : findings.Take(4).Select(f => new KvRow(
                StatusVerdictEvaluator.LabelOf(f.Level) + "　" + f.Source, f.RawValue)).ToList();

        // ---------- 快速跳转 ----------
        JumpList.ItemsSource = Jumps.Select(j => new JumpRow(j.Title, j.Desc, j.Tag)).ToList();
    }

    /// <summary>
    /// 介质转速的显示文本。
    ///
    /// **不能靠"转速为 null"反推固态**：IntelliPower 类机械盘（如 WD20EURS 这类 AV-GP 监控盘）
    /// 根本不报标称转速，靠转速猜会把机械盘标成"无（固态）"（实测踩中）。
    /// 正解是看介质判定结果（CmrSmrDetector 用机械盘独有属性算出来的）。
    /// </summary>
    private static string MediaSpeedText(IdentityInfo? id, MediaVerdict media)
    {
        if (id?.RotationRateRpm is { } rpm && rpm > 0) return rpm + " RPM";
        if (media.Technology == MediaTechnology.Ssd) return "无（固态）";
        if (media.Rotational) return "机械盘（转速未报告）";
        return "-";
    }

    /// <summary>
    /// 平均一次写命令搬多少字节 —— 写入总量 ÷ 主机写命令数。
    /// 这个数异常小（比如 < 8 KiB）说明是碎片写负载，对 SSD 的写入放大有实际影响；
    /// 也顺便验证了"主机写入量"与"写命令数"两个字段彼此自洽。
    /// </summary>
    private static string AverageWriteSize(NvmeSmartLog? n)
    {
        if (n is null || n.HostWriteCommands == 0) return "-";
        double bytes = UnitConverter.NvmeDataUnitsToBytes(n.DataUnitsWritten) / (double)n.HostWriteCommands;
        return UnitConverter.FormatBytes((ulong)Math.Max(0, bytes)) + "/命令";
    }

    /// <summary>
    /// 画温度历史。数据来自 data/history（每次诊断记一个点，同值同小时不重复记）。
    /// 纵轴不是从 0 起 —— 温度曲线的价值在"变化"，从 0 起会把 5 ℃ 的波动压成一条平线；
    /// 但波动小于 4 ℃ 时会给一段固定余量，免得把噪声放大成"剧烈起伏"。
    /// </summary>
    private void RenderTempHistory(MainViewModel vm)
    {
        var drive = vm.SelectedDrive;
        if (drive is null) return;
        var pts = _history.Query(drive.Model, drive.Serial, "Temperature", DateTime.Now.AddDays(-30));

        if (pts.Count < 2)
        {
            TempHistLine.Points = [];
            TempHistFill.Points = [];
            TempHistInfo.Text = pts.Count == 0 ? "暂无数据" : "只有 1 个点";
            TempHistRange.Text = pts.Count == 0
                ? "还没记录过 —— 每次诊断会自动落一个点（同值同小时不重复）。多跑几天就有曲线了。"
                : "再跑一次诊断就能连成线。";
            return;
        }

        double w = TempHistCanvas.ActualWidth, h = TempHistCanvas.ActualHeight;
        if (w <= 2 || h <= 2) return;

        double dmin = pts.Min(p => p.Value), dmax = pts.Max(p => p.Value);
        double lo = dmin, hi = dmax;
        if (hi - lo < 4) { double mid = (hi + lo) / 2; lo = mid - 2; hi = mid + 2; }
        var t0 = pts[0].When;
        double span = Math.Max(1, (pts[^1].When - t0).TotalSeconds);

        var line = new PointCollection();
        foreach (var (when, val) in pts)
        {
            double x = w * (when - t0).TotalSeconds / span;
            double y = h - 6 - (h - 14) * (val - lo) / (hi - lo);
            line.Add(new Point(x, y));
        }
        TempHistLine.Points = line;
        TempHistFill.Points = new PointCollection(line) { new Point(w, h), new Point(0, h) };

        TempHistInfo.Text = "最低 " + dmin.ToString("0.0") + " ℃ · 最高 " + dmax.ToString("0.0")
            + " ℃ · " + pts.Count + " 个采样点";
        TempHistRange.Text = t0.ToString("MM-dd HH:mm") + "  →  " + pts[^1].When.ToString("MM-dd HH:mm")
            + "　·　纵轴 " + lo.ToString("0.0") + " – " + hi.ToString("0.0") + " ℃";
    }

    private void OnJump(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) NavigateRequested?.Invoke(tag);
    }
}
