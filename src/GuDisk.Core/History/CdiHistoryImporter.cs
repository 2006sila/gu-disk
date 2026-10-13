using System.Globalization;

namespace GuDisk.Core.History;

/// <summary>
/// 导入 CrystalDiskInfo 的历史：直接读 <c>Smart/&lt;型号&gt;&lt;序列号&gt;/&lt;属性&gt;.csv</c>，
/// 因为我们的 HistoryStore 用的是同一套目录名与 CSV 格式，所以逐行复制即可。
/// 顺带解析同目录的 <c>Smart.ini</c>，把开机/电池等元信息留给调用方展示。
/// </summary>
public sealed class CdiHistoryImporter
{
    private const string TimeFormat = "yyyy/MM/dd HH:mm:ss";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly HistoryStore _store;

    public CdiHistoryImporter(HistoryStore? store = null) => _store = store ?? new HistoryStore();

    public CdiImportResult Import(string cdiSmartRoot, string? onlyFolder = null)
    {
        var notes = new List<string>();
        if (!Directory.Exists(cdiSmartRoot))
            return new CdiImportResult(0, 0, 0, ["目录不存在：" + cdiSmartRoot]);

        int folders = 0, files = 0, points = 0;
        foreach (var dir in Directory.GetDirectories(cdiSmartRoot))
        {
            string name = Path.GetFileName(dir);
            if (onlyFolder is not null && !name.Equals(onlyFolder, StringComparison.OrdinalIgnoreCase)) continue;
            folders++;

            string dst = Path.Combine(_store.Root, name);
            Directory.CreateDirectory(dst);

            foreach (var csv in Directory.GetFiles(dir, "*.csv"))
            {
                string baseName = Path.GetFileNameWithoutExtension(csv);
                if (baseName.Equals("attributes", StringComparison.OrdinalIgnoreCase)) continue;
                files++;

                var existing = new HashSet<string>(StringComparer.Ordinal);
                string dstFile = Path.Combine(dst, Path.GetFileName(csv));
                if (File.Exists(dstFile))
                    foreach (var l in File.ReadLines(dstFile)) existing.Add(l);

                int added = 0;
                foreach (var raw in File.ReadLines(csv))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || existing.Contains(line)) continue;
                    int c = line.LastIndexOf(',');
                    if (c <= 0) continue;
                    if (!DateTime.TryParseExact(line[..c].Trim(), TimeFormat, Inv, DateTimeStyles.None, out _)) continue;
                    if (!double.TryParse(line[(c + 1)..].Trim(), NumberStyles.Float, Inv, out _)) continue;
                    File.AppendAllText(dstFile, line + Environment.NewLine, System.Text.Encoding.UTF8);
                    existing.Add(line);
                    added++;
                }
                points += added;
            }

            string ini = Path.Combine(dir, "Smart.ini");
            if (File.Exists(ini)) notes.Add(name + "：含 Smart.ini（元信息，未解析）");
        }

        if (folders == 0) notes.Add("未找到任何 <型号><序列号> 目录");
        return new CdiImportResult(folders, files, points, notes);
    }
}
