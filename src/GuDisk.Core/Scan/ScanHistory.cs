using System.Text.Json;
using GuDisk.Core.App;

namespace GuDisk.Core.Scan;

/// <summary>
/// 扫描结果跨次对比。
///
/// 为什么值得做：**参考软件里没见谁做好这件事**（HDDScan / HDTune / Victoria 都是扫完即弃）。
/// 但对一块用了三年的盘，"这次扫出 12 个坏块"远不如"上次 0 个、这次 12 个"有信息量 ——
/// 与 Backblaze 那条"看变化速率而非绝对值"是同一个思路。
///
/// 坏块偏移与慢块偏移**分开存**：靠 ProblemBlocks 的天然顺序去猜哪几个是坏块是不可靠的。
/// 每类都有上限截断，避免一块满是坏道的盘把文件撑爆。
/// </summary>
public static class ScanHistory
{
    public const int MaxOffsets = 20_000;

    public sealed record Snapshot(string DiskKey, DateTime At, string Mode, long ScannedBytes,
        int ErrorCount, int SlowCount, long[] ErrorOffsets, long[] SlowOffsets, bool Truncated);

    public sealed record Diff(bool HasPrevious, DateTime? PreviousAt,
        int NewErrors, int GoneErrors, int NewSlow, int GoneSlow, bool PreviousTruncated)
    {
        public bool Same => NewErrors == 0 && GoneErrors == 0 && NewSlow == 0 && GoneSlow == 0;

        public string Text
        {
            get
            {
                if (!HasPrevious) return "首次扫描，无历史可对比";
                string when = "上次 " + PreviousAt!.Value.ToString("MM-dd HH:mm");
                if (Same) return "与" + when + "相比没有变化";
                var parts = new List<string>();
                if (NewErrors > 0) parts.Add("新增坏块 " + NewErrors + " 个");
                if (GoneErrors > 0) parts.Add("减少坏块 " + GoneErrors + " 个");
                if (NewSlow > 0) parts.Add("新增偏慢 " + NewSlow + " 个");
                if (GoneSlow > 0) parts.Add("减少偏慢 " + GoneSlow + " 个");
                return "与" + when + "相比：" + string.Join("　", parts)
                    + (PreviousTruncated ? "（上次未存全，数字偏保守）" : "");
            }
        }
    }

    private static string FilePath => System.IO.Path.Combine(AppPaths.Root, "scan-history.json");

    public static Snapshot? Load(string diskKey)
    {
        try
        {
            if (!System.IO.File.Exists(FilePath)) return null;
            var all = JsonSerializer.Deserialize<List<Snapshot>>(System.IO.File.ReadAllText(FilePath));
            return all?.FirstOrDefault(s => s.DiskKey == diskKey);
        }
        catch { return null; }
    }

    public static void Save(Snapshot snap)
    {
        try
        {
            var all = new List<Snapshot>();
            if (System.IO.File.Exists(FilePath))
                all = JsonSerializer.Deserialize<List<Snapshot>>(System.IO.File.ReadAllText(FilePath)) ?? [];
            all.RemoveAll(s => s.DiskKey == snap.DiskKey);   // 每盘只留最近一次
            all.Add(snap);
            // 不带 BOM：与 HistoryStore 一致。File.ReadAllText 虽然会自动剥离 BOM，
            // 但"有的文件带、有的不带"本身就是隐患，统一起来。
            string json = JsonSerializer.Serialize(all,
                new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(FilePath, json, new System.Text.UTF8Encoding(false));
        }
        catch { /* 存不上不影响扫描本身 */ }
    }

    public static Snapshot From(string diskKey, ScanResult res)
    {
        var errs = res.ProblemBlocks.Where(b => b.Class is BlockClass.Error or BlockClass.Timeout)
                                    .Select(b => b.OffsetBytes).ToArray();
        var slows = res.ProblemBlocks.Where(b => b.Class == BlockClass.Slow)
                                     .Select(b => b.OffsetBytes).ToArray();
        return new Snapshot(diskKey, DateTime.Now, res.Mode, res.ScannedBytes,
            res.ErrorCount, res.SlowCount,
            errs.Take(MaxOffsets).ToArray(), slows.Take(MaxOffsets).ToArray(),
            errs.Length > MaxOffsets || slows.Length > MaxOffsets);
    }

    /// <summary>
    /// 纯函数：比较两次扫描。抽出来是为了能单测 ——
    /// **把"减少"算成"新增"这类派生 bug 在这里最容易发生，也最容易被忽略**（数字看着很合理）。
    /// </summary>
    public static Diff Compare(Snapshot? prev, ScanResult cur)
    {
        if (prev is null) return new Diff(false, null, 0, 0, 0, 0, false);

        var nowErr = cur.ProblemBlocks.Where(b => b.Class is BlockClass.Error or BlockClass.Timeout)
                                      .Select(b => b.OffsetBytes).ToHashSet();
        var nowSlow = cur.ProblemBlocks.Where(b => b.Class == BlockClass.Slow)
                                       .Select(b => b.OffsetBytes).ToHashSet();
        var prevErr = prev.ErrorOffsets.ToHashSet();
        var prevSlow = prev.SlowOffsets.ToHashSet();

        return new Diff(true, prev.At,
            NewErrors: nowErr.Count(o => !prevErr.Contains(o)),
            GoneErrors: prevErr.Count(o => !nowErr.Contains(o)),
            NewSlow: nowSlow.Count(o => !prevSlow.Contains(o)),
            GoneSlow: prevSlow.Count(o => !nowSlow.Contains(o)),
            PreviousTruncated: prev.Truncated);
    }
}
