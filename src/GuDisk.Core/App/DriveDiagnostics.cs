using GuDisk.Core.Decode;
using GuDisk.Core.Devices;
using GuDisk.Core.Health;
using GuDisk.Core.Smart;

namespace GuDisk.Core.App;

/// <summary>一次完整诊断的全部产物（UI 的概览页只消费这一个对象）。</summary>
public sealed record DriveDiagnostic(
    PhysicalDriveInfo Drive,
    SmartReadOutcome Smart,
    MediaVerdict Media,
    StatusVerdict Verdict,
    string AttributeDbSource,
    string DrivedbSource,
    IReadOnlyList<string> Notes)
{
    public bool SmartAvailable => Smart.Success;
    public IReadOnlyList<CrossCheckItem> CrossCheck => Smart.CrossCheck;
    public IReadOnlyList<CrossCheckItem> Disagreements => Smart.CrossCheck.Where(c => !c.Agree).ToList();
}

/// <summary>把 识别 → SMART（双引擎） → 属性名套用 → 介质判定 → 状态判定 串成一条链。</summary>
public static class DriveDiagnostics
{
    public static DriveDiagnostic Run(PhysicalDriveInfo drive, bool crossCheck = true, string protocol = "auto",
        SmartAttributeDb? attrDb = null, DrivedbIndex? drivedb = null)
    {
        var notes = new List<string>();
        attrDb ??= SmartAttributeDb.Load();
        drivedb ??= DrivedbIndex.Load();

        var smart = new SmartReadOrchestrator().Read(drive, crossCheck, protocol,
            string.IsNullOrWhiteSpace(AppSettings.Current.DeviceTypeOverride) ? null : AppSettings.Current.DeviceTypeOverride);

        // 属性名：NVMe 表已内建；ATA 用 通用表 + 厂商覆盖
        bool isNvme = smart.Nvme is not null;
        if (!isNvme && smart.Attributes.Count > 0 && attrDb is not null)
        {
            var list = smart.Attributes.ToList();
            int changed = attrDb.ApplyNames(list, drive.Model, false);
            smart = smart with { Attributes = list };
            notes.Add("特征库覆盖属性名 " + changed + " 项（厂商节：" +
                (attrDb.MatchVendor(drive.Model)?.Id ?? "未命中，使用通用表") + "）");
        }

        var media = CmrSmrDetector.Detect(drive.Model, smart.Identity, smart.Nvme, smart.Ata, drivedb);

        // 状态判定：纯谓词，逐条可追溯到 SMART 字段（不再折算分数）
        // 阈值改为可配置（设置页），默认仍是 55/65 ℃
        // 历史趋势注入：判定器保持"纯谓词"，趋势通过委托查（不把文件 I/O 塞进判定逻辑，
        // 也方便单测时传假数据）。
        var history = new GuDisk.Core.History.HistoryStore();
        GuDisk.Core.History.HistoryStore.Trend? TrendOf(string metric)
            => history.TrendOf(drive.Model, drive.Serial, metric, 30);
        DateTime? FirstPositiveOf(string metric)
            => history.FirstPositiveAt(drive.Model, drive.Serial, metric);

        var verdict = StatusVerdictEvaluator.Evaluate(smart,
            AppSettings.Current.TempWarnC, AppSettings.Current.TempCritC, 30000,
            TrendOf, FirstPositiveOf);

        if (attrDb is null) notes.Add("未找到 data/smart-attributes.json —— 属性名仅用内建表（先跑 tools/convert-lang.mjs）");
        if (drivedb is null) notes.Add("未找到 data/drivedb-index.json —— 介质判定缺官方标注（先跑 tools/convert-drivedb.mjs）");

        return new DriveDiagnostic(drive, smart, media, verdict,
            attrDb?.Source ?? "(未加载)", drivedb?.Source ?? "(未加载)", notes);
    }
}
