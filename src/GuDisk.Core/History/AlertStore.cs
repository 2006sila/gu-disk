using System.Text;
using System.Text.Json;
using GuDisk.Core.App;

namespace GuDisk.Core.History;

/// <summary>
/// 告警历史落盘（JSONL，追加写）。
/// CDI 有 ALARM_HISTORY，之前我们只在内存里留最近 50 条 —— 关掉程序就没了。
/// 追加写的取舍：不做原子替换，因为告警是"只增不改"的流水，最坏情况只是丢了最后一行。
/// </summary>
public sealed class AlertStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };
    private readonly string _path;

    public AlertStore(string? path = null)
        => _path = path ?? System.IO.Path.Combine(AppPaths.Root, "alerts.jsonl");   // 属性名 Path 会遮蔽 System.IO.Path

    public string Path => _path;

    public void Append(AlertItem a)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            string line = JsonSerializer.Serialize(new
            {
                when = a.When.ToString("yyyy-MM-dd HH:mm:ss"),
                level = a.Level.ToString(),
                kind = a.Kind.ToString(),
                disk = a.DiskKey,
                model = a.Model,
                title = a.Title,
                detail = a.Detail,
            }, Opts);
            File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { /* 落盘失败不影响主流程 */ }
    }

    /// <summary>读最近 N 条（新的在前）。</summary>
    public IReadOnlyList<AlertItem> Recent(int count = 200)
    {
        try
        {
            if (!File.Exists(_path)) return [];
            var lines = File.ReadAllLines(_path);
            var list = new List<AlertItem>();
            for (int i = lines.Length - 1; i >= 0 && list.Count < count; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                try
                {
                    using var doc = JsonDocument.Parse(lines[i]);
                    var r = doc.RootElement;
                    list.Add(new AlertItem(
                        Enum.TryParse<AlertKind>(Str(r, "kind"), out var k) ? k : AlertKind.Temperature,
                        Enum.TryParse<AlertLevel>(Str(r, "level"), out var lv) ? lv : AlertLevel.Info,
                        Str(r, "disk"), Str(r, "model"), Str(r, "title"), Str(r, "detail"),
                        DateTime.TryParse(Str(r, "when"), out var w) ? w : DateTime.Now));
                }
                catch { }
            }
            return list;
        }
        catch { return []; }
    }

    public int Count()
    {
        try { return File.Exists(_path) ? File.ReadAllLines(_path).Length : 0; } catch { return 0; }
    }

    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
}
