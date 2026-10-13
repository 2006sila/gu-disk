using System.Text.Json;
using System.Text.RegularExpressions;

namespace GuDisk.Core.Decode;

/// <summary>
/// drivedb.h 的型号索引（由 <c>tools/convert-drivedb.mjs</c> 生成）。
/// 用途：① 官方 "(SMR)"/"(CMR)" 标注 → 介质类型判定 100% 置信度的依据；② 型号族名。
/// 注意：drivedb 里是 C 的正则，.NET 与它基本兼容；编译失败的条目直接跳过（不影响其它条目）。
/// </summary>
public sealed class DrivedbIndex
{
    public sealed record Entry(string Family, string Pattern, bool Smr, bool Cmr);

    private readonly List<(Entry Entry, Regex? Rx)> _entries = [];

    public string Source { get; } = "";
    public int Total { get; }
    public int SmrCount { get; }

    private DrivedbIndex(string source, int total, int smr, List<(Entry, Regex?)> entries)
    {
        Source = source; Total = total; SmrCount = smr; _entries = entries;
    }

    public static DrivedbIndex? Load(string? jsonPath = null)
    {
        string path = jsonPath ?? Path.Combine(Core.App.AppPaths.FindProductData() ?? "", "drivedb-index.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var list = new List<(Entry, Regex?)>();
            if (root.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                {
                    var entry = new Entry(
                        e.TryGetProperty("family", out var f) ? (f.GetString() ?? "") : "",
                        e.TryGetProperty("pattern", out var p) ? (p.GetString() ?? "") : "",
                        e.TryGetProperty("smr", out var s) && s.ValueKind == JsonValueKind.True,
                        e.TryGetProperty("cmr", out var c) && c.ValueKind == JsonValueKind.True);
                    Regex? rx = null;
                    if (entry.Pattern.Length > 0)
                    {
                        try { rx = new Regex(entry.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
                        catch { }
                    }
                    list.Add((entry, rx));
                }
            return new DrivedbIndex(
                root.TryGetProperty("source", out var src) ? (src.GetString() ?? "") : "",
                list.Count,
                list.Count(x => x.Item1.Smr),
                list);
        }
        catch { return null; }
    }

    /// <summary>按型号找条目标；<paramref name="wantSmr"/> 为 true 时只找 SMR 条目（找 CMR 同理）。</summary>
    public Entry? Match(string model, bool? smrOnly = null, bool? cmrOnly = null)
    {
        string m = (model ?? "").Trim();
        if (m.Length == 0) return null;
        foreach (var (entry, rx) in _entries)
        {
            if (smrOnly == true && !entry.Smr) continue;
            if (cmrOnly == true && !entry.Cmr) continue;
            if (rx is null) continue;
            try { if (rx.IsMatch(m)) return entry; } catch { }
        }
        return null;
    }
}
