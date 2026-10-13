using System.Globalization;
using System.Text;

namespace GuDisk.Core.History;

/// <summary>
/// 历史落盘。**目录名直接用「型号+序列号」**（与 CrystalDiskInfo 的 Smart/&lt;型号&gt;&lt;序列号&gt;/ 完全同名），
/// 这样导入 CDI 历史就是零成本的文件级合并。
/// 文件格式也与 CDI 对齐：每行 <c>yyyy/MM/dd HH:mm:ss,值</c>。
/// </summary>
public sealed class HistoryStore
{
    private const string TimeFormat = "yyyy/MM/dd HH:mm:ss";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Root { get; }

    public HistoryStore(string? root = null) => Root = root ?? Core.App.AppPaths.History;

    /// <summary>型号+序列号 → 目录名（去掉非法字符；与 CDI 命名保持一致）。</summary>
    public static string FolderFor(string model, string serial)
    {
        var sb = new StringBuilder();
        foreach (char c in (model ?? "") + (serial ?? ""))
            if (!Path.GetInvalidFileNameChars().Contains(c)) sb.Append(c);
        string s = sb.ToString().Trim();
        return s.Length == 0 ? "UNKNOWN" : s;
    }

    public string FolderOf(string model, string serial) => Path.Combine(Root, FolderFor(model, serial));

    public void Ensure(string model, string serial)
        => Directory.CreateDirectory(FolderOf(model, serial));

    /// <summary>追加一个采样点（同秒去重）。</summary>
    public bool Append(string model, string serial, string metric, DateTime when, double value)
    {
        try
        {
            string dir = FolderOf(model, serial);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, SafeName(metric) + ".csv");
            string line = when.ToString(TimeFormat, Inv) + "," + Format(value);

            if (File.Exists(file))
            {
                string[] tail = File.ReadLines(file).TakeLast(1).ToArray();
                if (tail.Length == 1 && tail[0] == line) return false;
            }
            // **必须用不带 BOM 的编码**：Encoding.UTF8 的静态实例会写 3 字节 preamble，
            // 而 Query 用 TryParseExact 解析首行时会被 BOM 挡住 →
            // 每个历史文件的**第一个点都被静默丢弃**（实测：单行文件 29 字节 = 26 + BOM 3）。
            File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    /// <summary>按时间区间读取一个指标。</summary>
    public IReadOnlyList<(DateTime When, double Value)> Query(string model, string serial,
        string metric, DateTime? from = null, DateTime? to = null)
    {
        var list = new List<(DateTime, double)>();
        string file = Path.Combine(FolderOf(model, serial), SafeName(metric) + ".csv");
        if (!File.Exists(file)) return list;

        foreach (var raw in File.ReadLines(file))
        {
            // 兼容历史遗留：早期写入的文件首行带 UTF-8 BOM，这里剥掉再解析
            string line = raw.TrimStart('\uFEFF');
            int c = line.LastIndexOf(',');
            if (c <= 0) continue;
            if (!DateTime.TryParseExact(line[..c].Trim(), TimeFormat, Inv,
                    DateTimeStyles.None, out var t)) continue;
            if (!double.TryParse(line[(c + 1)..].Trim(), NumberStyles.Float, Inv, out var v)) continue;
            if (from is not null && t < from) continue;
            if (to is not null && t > to) continue;
            list.Add((t, v));
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    /// <summary>某个指标在窗口内的变化趋势。**这是 Backblaze 那条结论的落地形式** ——
    /// "一天涨 20" 与 "五年涨 60" 的绝对值可能一样，但完全是两种盘。</summary>
    public sealed record Trend(string Metric, int Points, DateTime FirstAt, DateTime LastAt,
        double First, double Last)
    {
        public double Delta => Last - First;
        public bool Grew => Delta > 0;
        public double Days => Math.Max(0.04, (LastAt - FirstAt).TotalDays);
    }

    /// <summary>窗口内的趋势；样本不足 2 个点返回 null（**不猜**）。</summary>
    public Trend? TrendOf(string model, string serial, string metric, int days)
    {
        var pts = Query(model, serial, metric, DateTime.Now.AddDays(-days));
        if (pts.Count < 2) return null;
        var f = pts[0];
        var l = pts[^1];
        return new Trend(metric, pts.Count, f.When, l.When, f.Value, l.Value);
    }

    /// <summary>
    /// 首次出现正值的时间点。用于"首次不可纠正错误之后的 60 天窗口"这类统计规则
    /// （Google FAST'07 的结论：首次不可纠正错误后的 60 天内故障率显著升高）。
    /// 历史上从未有过正值 → null。
    /// </summary>
    public DateTime? FirstPositiveAt(string model, string serial, string metric)
    {
        foreach (var (when, v) in Query(model, serial, metric))
            if (v > 0) return when;
        return null;
    }

    /// <summary>已记录历史的所有盘。</summary>
    public IReadOnlyList<string> ListFolders()
        => Directory.Exists(Root)
            ? Directory.GetDirectories(Root).Select(Path.GetFileName).Where(x => x is not null).Cast<string>().ToList()
            : [];

    private static string SafeName(string metric)
        => string.Concat(metric.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>整数值不写小数点，与 CDI 的 CSV 一致。</summary>
    private static string Format(double v)
        => Math.Abs(v - Math.Round(v)) < 1e-9 ? ((long)Math.Round(v)).ToString(Inv) : v.ToString("0.###", Inv);
}

/// <summary>导入结果。</summary>
public sealed record CdiImportResult(int Folders, int Files, int Points, IReadOnlyList<string> Notes);
