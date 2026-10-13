namespace GuDisk.Core.App;

public enum AlertKind { Temperature, StateChange, CriticalAttribute, SmartFailing, MediaError }
public enum AlertLevel { Info, Warning, Critical }

public sealed record AlertRule(
    double TempWarning = 55,
    double TempCritical = 65,
    bool AlertOnGradeDrop = true,
    bool AlertOnCriticalAttribute = true)
{
    public static AlertRule Default { get; } = new();
}

/// <summary>一条告警。</summary>
public sealed record AlertItem(AlertKind Kind, AlertLevel Level, string DiskKey, string Model,
    string Title, string Detail, DateTime When);

/// <summary>一块盘上一次采样留下的状态，用于判断"变化"而不是"状态"。</summary>
public sealed record DiskAlertState(
    string DiskKey,
    string Model,
    Health.DriveState? LastState,
    double? LastTemperature,
    bool? LastSmartPassed,
    long CriticalAttributeCount);

/// <summary>
/// 告警评估。**只在状态变化时触发** —— 温度持续偏高只报一次，恢复了再报一次"恢复"，
/// 否则常驻监控会每隔几秒刷一条同样的通知。
/// </summary>
public static class AlertEvaluator
{
    /// <summary>评估一次采样，返回需要新发的告警。</summary>
    public static IReadOnlyList<AlertItem> Evaluate(
        DiskAlertState? previous,
        string diskKey, string model, Health.DriveState state, double? temperatureC,
        bool smartPassed, long criticalAttributes, long mediaErrors,
        AlertRule? rule = null, DateTime? now = null)
    {
        var r = rule ?? AlertRule.Default;
        var t = now ?? DateTime.Now;
        var list = new List<AlertItem>();

        void Add(AlertKind k, AlertLevel lv, string title, string detail)
            => list.Add(new AlertItem(k, lv, diskKey, model, title, detail, t));

        // 温度：只在跨越阈值时触发
        if (temperatureC is { } temp)
        {
            bool wasOver = previous?.LastTemperature is { } pt && pt >= r.TempWarning;
            if (temp >= r.TempCritical)
                Add(AlertKind.Temperature, AlertLevel.Critical, "温度过高",
                    model + " 当前 " + temp.ToString("0.#") + " ℃（危险阈值 " + r.TempCritical + " ℃）");
            else if (temp >= r.TempWarning && !wasOver)
                Add(AlertKind.Temperature, AlertLevel.Warning, "温度偏高",
                    model + " 当前 " + temp.ToString("0.#") + " ℃（注意阈值 " + r.TempWarning + " ℃）");
            else if (temp < r.TempWarning && wasOver)
                Add(AlertKind.Temperature, AlertLevel.Info, "温度恢复",
                    model + " 已回落到 " + temp.ToString("0.#") + " ℃");
        }

        // 状态档位变化（注意 < 警告 < 危急 为恶化方向）
        if (r.AlertOnGradeDrop && previous?.LastState is { } ps && ps != state)
        {
            var label = Health.StatusVerdictEvaluator.LabelOf;
            if (state > ps)   // 枚举按严重度升序：值变大 = 恶化
                Add(AlertKind.StateChange, state >= Health.DriveState.Warning ? AlertLevel.Critical : AlertLevel.Warning,
                    "状态降级为「" + label(state) + "」", model + " 从「" + label(ps) + "」变为「" + label(state) + "」");
            else
                Add(AlertKind.StateChange, AlertLevel.Info, "状态回到「" + label(state) + "」",
                    model + " 从「" + label(ps) + "」恢复到「" + label(state) + "」");
        }

        // SMART 整体判定翻转
        if (previous?.LastSmartPassed is { } was && was && !smartPassed)
            Add(AlertKind.SmartFailing, AlertLevel.Critical, "SMART 判定转为失败",
                model + " 的 SMART 整体状态从通过变为失败，请立即备份");

        // 关键属性新增
        if (r.AlertOnCriticalAttribute && previous is not null
            && criticalAttributes > previous.CriticalAttributeCount)
            Add(AlertKind.CriticalAttribute, AlertLevel.Warning, "关键属性出现新增异常",
                model + " 关键项从 " + previous.CriticalAttributeCount + " 增至 " + criticalAttributes);

        // 介质错误
        if (mediaErrors > 0 && (previous is null || previous.CriticalAttributeCount == 0))
            Add(AlertKind.MediaError, AlertLevel.Critical, "出现介质错误",
                model + " 报告 " + mediaErrors + " 条介质与数据完整性错误");

        return list;
    }

    /// <summary>把当前状态/温度折算成下一次评估需要的快照。</summary>
    public static DiskAlertState Snapshot(string diskKey, string model, Health.DriveState state,
        double? temperatureC, bool smartPassed, long criticalAttributes)
        => new(diskKey, model, state, temperatureC, smartPassed, criticalAttributes);
}
