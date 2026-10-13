using GuDisk.Core.History;
using GuDisk.Core.Smart;

namespace GuDisk.Core.Health;

/// <summary>盘的运行状态。四级，由 SMART 谓词直接判定，<b>不做任何加权折算</b>。</summary>
public enum DriveState { Normal, Attention, Warning, Critical }

/// <summary>
/// 一条判定依据。三段式：**来源属性 → 原始值 → 判读**。
/// 每一行都能指回一个真实存在的 SMART 字段，不存在"综合扣分"这类中间量。
/// </summary>
public sealed record VerdictItem(
    DriveState Level,
    string Source,
    string RawValue,
    string Reading,
    string? Advice = null);

public sealed record StatusVerdict(
    DriveState State,
    string Label,
    string Summary,
    IReadOnlyList<VerdictItem> Findings,
    IReadOnlyList<VerdictItem> Checks,
    bool SmartAvailable)
{
    public int CriticalCount => Findings.Count(f => f.Level == DriveState.Critical);
    public int WarningCount => Findings.Count(f => f.Level == DriveState.Warning);
    public int AttentionCount => Findings.Count(f => f.Level == DriveState.Attention);

    public string CountsText => Findings.Count == 0
        ? "已核对 " + Checks.Count + " 项，全部正常"
        : "严重 " + CriticalCount + " · 警告 " + WarningCount + " · 注意 " + AttentionCount;
}

/// <summary>
/// 状态判定。**只做谓词判断，不做算术综合** —— 早期版本把各项折算成 0-100 分，
/// 那个数字是公式产物而非盘的客观事实，已废弃。
///
/// 每条规则的原始值都直接取自 SMART 字段，界面与报告逐条展示，便于人工复核。
/// </summary>
public static class StatusVerdictEvaluator
{
    /// <param name="trendOf">按指标名取最近 30 天的趋势；传 null 表示没有历史（**不猜**）。</param>
    /// <param name="firstPositiveOf">按指标名取"首次出现正值"的时间，用于 60 天窗口统计。</param>
    public static StatusVerdict Evaluate(SmartReadOutcome smart,
        double tempWarn = 55, double tempCritical = 70, long powerOnWarnHours = 30000,
        Func<string, HistoryStore.Trend?>? trendOf = null,
        Func<string, DateTime?>? firstPositiveOf = null)
    {
        var findings = new List<VerdictItem>();
        var checks = new List<VerdictItem>();

        void Hit(DriveState lv, string source, string raw, string reading, string? advice = null)
            => findings.Add(new VerdictItem(lv, source, raw, reading, advice));
        void Ok(string source, string raw, string reading)
            => checks.Add(new VerdictItem(DriveState.Normal, source, raw, reading, null));

        if (!smart.Success)
        {
            Hit(DriveState.Attention, "SMART 读取", smart.Error ?? "失败",
                "读不到 SMART 数据 —— 本页各项判定均无依据，结论仅供参考",
                "换一条 USB 线/换直连 SATA 口重试；USB 盒往往不转发 SMART");
            return Build(findings, checks, false);
        }

        if (smart.Nvme is { } n) EvaluateNvme(n, tempWarn, tempCritical, powerOnWarnHours, Hit, Ok);
        else if (smart.Ata is { } a) EvaluateAta(a, smart, tempWarn, tempCritical, powerOnWarnHours, Hit, Ok,
            trendOf, firstPositiveOf);
        else
            Hit(DriveState.Attention, "SMART 数据", "空", "没有解析出可用的属性表", "确认盘型识别是否正确");

        return Build(findings, checks, true);
    }

    // ---------------- NVMe ----------------
    private static void EvaluateNvme(NvmeSmartLog n, double tWarn, double tCrit, long powerWarn,
        Action<DriveState, string, string, string, string?> hit, Action<string, string, string> ok)
    {
        // 0x01 严重警告标志
        if (n.CriticalWarning != 0)
            hit(DriveState.Critical, "0x01 严重警告标志", "0x" + n.CriticalWarning.ToString("X2"),
                "固件已主动报告严重状态 —— 可用备用空间耗尽或已进入只读模式",
                "立即备份数据并更换硬盘");
        else ok("0x01 严重警告标志", "0", "固件未报告任何严重状态");

        // 0x0E 介质与数据完整性错误
        if (n.MediaErrors > 0)
            hit(DriveState.Critical, "0x0E 介质与数据完整性错误计数", n.MediaErrors.ToString(),
                "出现过 " + n.MediaErrors + " 次介质错误 —— 数据完整性已受影响",
                "立即备份；若持续增长直接更换");
        else ok("0x0E 介质与数据完整性错误计数", "0", "未出现介质错误");

        // 0x03 / 0x04 可用备用空间与阈值
        if (n.AvailableSpare < n.AvailableSpareThreshold)
            hit(DriveState.Critical, "0x03 可用备用空间", n.AvailableSpare + "%（阈值 " + n.AvailableSpareThreshold + "%）",
                "备用块已低于固件阈值 —— 后续写入可能被拒绝", "立即备份并更换");
        else if (n.AvailableSpare < 20)
            hit(DriveState.Warning, "0x03 可用备用空间", n.AvailableSpare + "%（阈值 " + n.AvailableSpareThreshold + "%）",
                "备用块消耗过半，磨损进入后段", "备份并开始物色替换盘");
        else ok("0x03 可用备用空间", n.AvailableSpare + "%（阈值 " + n.AvailableSpareThreshold + "%）", "备用块充足");

        // 0x05 已用寿命
        if (n.PercentageUsed >= 95)
            hit(DriveState.Critical, "0x05 已用寿命百分比", n.PercentageUsed + "%", "写入寿命基本耗尽", "更换硬盘");
        else if (n.PercentageUsed >= 80)
            hit(DriveState.Warning, "0x05 已用寿命百分比", n.PercentageUsed + "%", "寿命进入末段（≥80%）", "安排更换");
        else ok("0x05 已用寿命百分比", n.PercentageUsed + "%", "寿命余量充足");

        // 0x02 温度
        if (n.CompositeTemperatureC is { } t)
        {
            string raw = t.ToString("0.#") + " ℃";
            if (t >= tCrit) hit(DriveState.Warning, "0x02 综合温度", raw, "超过危险阈值 " + tCrit + " ℃，固件可能降频", "改善机箱风道或加散热片");
            else if (t >= tWarn) hit(DriveState.Attention, "0x02 综合温度", raw, "超过注意阈值 " + tWarn + " ℃", "关注持续高负载下的温升");
            else ok("0x02 综合温度", raw, "温度正常");
        }

        // 0x02 延伸 · **温度累计时间** —— NVMe 健康日志里的累计字段。
        // 当前温度只说明"此刻热不热"，累计时间才说明"散热是不是一直压不住"。
        if (n.CriticalTempTimeMinutes > 0 || n.WarningTempTimeMinutes > 0)
            hit(DriveState.Attention, "0x02 温度累计时间",
                "警告 " + n.WarningTempTimeMinutes + " 分钟 · 临界 " + n.CriticalTempTimeMinutes + " 分钟",
                n.CriticalTempTimeMinutes > 0
                    ? "盘曾在临界温度以上运行 —— 散热余量不足"
                    : "盘曾达到警告温度",
                "改善机箱风道或加散热片");
        else ok("0x02 温度累计时间", "0 分钟", "从未越过警告温度");

        // 温控降频：**只展示，不判级**。
        // NVMe 规范没有规定"降频多少次算多"，在拿到真实盘的基线之前不编造阈值。
        int thTotal = (int)(n.ThermalTransition1 + n.ThermalTransition2);
        ok("温控降频记录",
            "T1 " + n.ThermalTransition1 + " 次/" + n.ThermalTime1Minutes + " 分 · T2 "
            + n.ThermalTransition2 + " 次/" + n.ThermalTime2Minutes + " 分",
            thTotal > 0 ? "曾触发 " + thTotal + " 次温控降频（仅展示，暂不判级）" : "从未触发温控降频");

        // 0x0D 不安全关机
        // 不安全关机是**使用习惯**指标，不是介质缺陷 —— 不该与数据风险同级，最高只到"警告"
        if (n.UnsafeShutdowns >= 1000)
            hit(DriveState.Warning, "0x0D 不安全关机计数", n.UnsafeShutdowns + " 次",
                "长期反复非正常掉电，FTL 映射表重建次数很多", "排查供电与休眠策略；SSD 本身无直接损伤");
        else if (n.UnsafeShutdowns >= 100)
            hit(DriveState.Attention, "0x0D 不安全关机计数", n.UnsafeShutdowns + " 次",
                "非正常掉电偏多（使用习惯，不代表介质有问题）", "启用安全弹出、关闭快速启动");
        else ok("0x0D 不安全关机计数", n.UnsafeShutdowns + " 次", "掉电记录在正常范围");

        // 0x0A 控制器忙时间
        if (n.ControllerBusyTime >= 1440)
            hit(DriveState.Attention, "0x0A 控制器忙状态时间", n.ControllerBusyTime + " 分钟（≥24 小时）",
                "控制器长期处于高负载", "确认是否有异常后台写入");
        else ok("0x0A 控制器忙状态时间", n.ControllerBusyTime + " 分钟", "控制器负载正常");

        // 0x0F 错误日志条目数
        if (n.ErrorLogEntries > 0)
            hit(DriveState.Attention, "0x0F 错误日志条目数", n.ErrorLogEntries + " 条", "固件记录过错误条目", "可用 selftest-log 查看详情");
        else ok("0x0F 错误日志条目数", "0", "固件错误日志为空");

        // 0x0C 通电时间
        long hours = (long)n.PowerOnHours;
        if (hours >= 50000) hit(DriveState.Warning, "0x0C 通电时间", hours + " 小时", "远超消费级设计寿命（约 5 万小时）", "计划更换");
        else if (hours >= powerWarn) hit(DriveState.Attention, "0x0C 通电时间", hours + " 小时", "服役时间较长", "关注其它增长项");
        else ok("0x0C 通电时间", hours + " 小时", "服役时间正常");
    }

    // ---------------- ATA ----------------
    private static void EvaluateAta(AtaSmartBlock ata, SmartReadOutcome smart, double tWarn, double tCrit, long powerWarn,
        Action<DriveState, string, string, string, string?> hit, Action<string, string, string> ok,
        Func<string, HistoryStore.Trend?>? trendOf = null,
        Func<string, DateTime?>? firstPositiveOf = null)
    {
        SmartAttribute? A(int id) => ata.Attributes.FirstOrDefault(x => x.Id == id);
        string Name(int id, string fallback) => A(id)?.NameCn ?? fallback;

        if (!ata.SmartPassed)
            hit(DriveState.Critical, "SMART 整体判定", "失败",
                "盘自身的 SMART 自评不是「通过」", "立即备份并更换");
        else ok("SMART 整体判定", "通过", "盘自评为通过");

        void ById(int id, string fallback, DriveState level, string reading, string advice)
        {
            var a = A(id);
            if (a is null) return;
            if (a.Raw > 0) hit(level, "0x" + id.ToString("X2") + " " + Name(id, fallback), a.Raw.ToString(), reading, advice);
            else ok("0x" + id.ToString("X2") + " " + Name(id, fallback), "0", "计数为 0");
        }

        ById(0x05, "重映射扇区计数", DriveState.Critical, "已有扇区被重映射 —— 盘面出现坏道", "立即备份并更换");
        ById(0xC5, "当前待处理扇区数", DriveState.Critical, "存在待重映射的不稳定扇区，读它会卡顿", "立即备份");
        ById(0xC6, "无法纠正的错误计数", DriveState.Critical, "出现过主机无法纠正的读取错误", "立即备份");
        ById(0xC7, "UDMA CRC 错误计数", DriveState.Warning, "传输层校验失败 —— 通常是线缆或接口", "换 SATA 线与口，与坏道无关");
        ById(0xB5, "报告不可纠正错误", DriveState.Warning, "记录到不可纠正错误", "备份并观察增长");
        ById(0xBB, "报告的不可纠正错误", DriveState.Warning, "记录到不可纠正错误", "备份并观察增长");
        ById(0x0A, "旋转重试计数", DriveState.Warning, "主轴启动/稳定出现重试 —— 机械结构老化", "备份并计划更换");
        // 2026-10-13 补：这三条此前只在属性表里能看到，判定器没覆盖
        ById(0xBC, "命令超时", DriveState.Attention,
            "出现过超时未完成的命令 —— 常见于线缆或供电，也可能是盘体响应退化", "先换 SATA 线/口，再观察是否增长");
        ById(0xB8, "端到端错误", DriveState.Attention,
            "数据通路中检测到并纠正了位错误", "备份并观察增长");
        ById(0xBD, "高飞写入", DriveState.Attention,
            "记录头曾飞离正常高度 —— 机械稳定性问题（Backblaze 指出成簇出现时值得警惕）", "备份并观察是否成簇出现");
        // 0xC0 断电磁头归位：**每次正常断电都会计数**，非零属预期，只展示不判异常 ——
        // 把它做成判定项就是纯噪音（与"不惊悚"的约定冲突）。
        if (A(0xC0) is { } retract)
            ok("0xC0 断电磁头归位", retract.Raw.ToString(), "随断电次数累加，非异常指标");

        // 温度：0xC2 优先，没有就退到 0xBE 气流温度（有些盘只报后者）
        var temp = A(0xC2) ?? A(0xBE);
        if (temp is not null)
        {
            string tag = "0x" + temp.Id.ToString("X2") + " " + Name(temp.Id, "温度");
            double t = temp.Raw;
            string raw = t.ToString("0") + " ℃";
            if (t >= tCrit) hit(DriveState.Warning, tag, raw, "超过危险阈值 " + tCrit + " ℃", "改善散热");
            else if (t >= tWarn) hit(DriveState.Attention, tag, raw, "超过注意阈值 " + tWarn + " ℃", "关注散热");
            else ok(tag, raw, "温度正常");
        }

        var poh = A(0x09);
        if (poh is not null)
        {
            if ((long)poh.Raw >= 50000) hit(DriveState.Warning, "0x09 通电时间", poh.Raw + " 小时", "机械盘设计寿命约 5 万小时", "计划更换");
            else if ((long)poh.Raw >= powerWarn) hit(DriveState.Attention, "0x09 通电时间", poh.Raw + " 小时", "服役时间较长", "关注其它增长项");
            else ok("0x09 通电时间", poh.Raw + " 小时", "服役时间正常");
        }

        // ---- 变化速率（Backblaze 的核心结论）----
        // 绝对值一样、变化速率不同的两块盘不是一回事：
        // "三年里慢慢涨到 60" 与 "一天从 0 涨到 20"，后者危险得多。
        // 只看绝对值只能回答"有没有坏道"，配合历史才能回答"坏道是新的还是老的"。
        void Growth(int id, string metric, string what)
        {
            if (trendOf?.Invoke(metric) is not { Grew: true } t || t.Delta < 1) return;
            hit(DriveState.Warning, "0x" + id.ToString("X2") + " " + Name(id, what) + " · 增长",
                "+" + t.Delta.ToString("0") + "（" + t.First.ToString("0") + " → " + t.Last.ToString("0") + "）",
                "最近 " + t.Days.ToString("0") + " 天内新增 —— **正在持续劣化**，"
                + "比历史遗留的稳定计数更值得警惕（Google：" + what + " 属于强预测指标）",
                "立即备份，并缩短复查周期");
        }
        Growth(0x05, "Reallocated", "重映射扇区计数");
        Growth(0xC5, "Pending", "当前待处理扇区数");
        Growth(0xC6, "Uncorrectable", "无法纠正的错误计数");

        // ---- 首次不可纠正错误后的 60 天窗口（Google FAST'07）----
        // 措辞刻意写成"统计相关性"，不写成"你的盘一定会坏" —— 不惊悚是既定原则。
        if (firstPositiveOf?.Invoke("Uncorrectable") is { } firstAt)
        {
            double days = (DateTime.Now - firstAt).TotalDays;
            if (days <= 60)
                hit(DriveState.Warning, "0xC6 无法纠正的错误 · 窗口期",
                    "首次出现在 " + firstAt.ToString("yyyy-MM-dd") + "（" + days.ToString("0") + " 天前）",
                    "大规模统计显示：首次出现无法纠正错误后的 60 天内，故障率显著高于同类盘 —— "
                    + "这是统计相关性，不是必然，但这个窗口期值得当成高风险对待",
                    "立刻备份全部重要数据");
        }

        // 数据存疑（双引擎交叉校验不一致）
        foreach (var c in smart.CrossCheck.Where(c => !c.Agree))
            hit(DriveState.Attention, "交叉校验 · " + c.Field, c.Native + " vs " + c.Smartctl,
                "两个引擎读到的值不一致，该字段存疑", c.Note);
    }

    private static StatusVerdict Build(List<VerdictItem> findings, List<VerdictItem> checks, bool smartOk)
    {
        var order = new[] { DriveState.Critical, DriveState.Warning, DriveState.Attention, DriveState.Normal };
        findings.Sort((a, b) => Array.IndexOf(order, a.Level).CompareTo(Array.IndexOf(order, b.Level)));

        var state = findings.Count == 0 ? DriveState.Normal : findings[0].Level;
        string label = LabelOf(state);
        string summary = state switch
        {
            DriveState.Normal => "所有已核对的 SMART 项都在正常范围，未发现异常。",
            DriveState.Attention => "有 " + findings.Count + " 项需要关注 —— 不影响继续使用，但建议观察其增长趋势。",
            DriveState.Warning => "有 " + findings.Count + " 项异常（含 " + findings.Count(f => f.Level == DriveState.Warning)
                + " 项警告）—— 建议尽快备份重要数据。",
            _ => "有 " + findings.Count(f => f.Level == DriveState.Critical)
                + " 项严重异常 —— 立即备份数据并更换硬盘。",
        };
        if (!smartOk) summary = "SMART 数据不可读，无法判定 —— " + summary;

        return new StatusVerdict(state, label, summary, findings, checks, smartOk);
    }

    public static string LabelOf(DriveState s) => s switch
    {
        DriveState.Normal => "正常",
        DriveState.Attention => "注意",
        DriveState.Warning => "警告",
        _ => "危急",
    };

    public static string ColorTokenOf(DriveState s) => s switch
    {
        DriveState.Normal => "Brush.Ok",
        DriveState.Attention => "Brush.Warn",
        DriveState.Warning => "Brush.Warn",
        _ => "Brush.Bad",
    };
}
