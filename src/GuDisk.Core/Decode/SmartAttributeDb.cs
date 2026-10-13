using System.Text.Json;

namespace GuDisk.Core.Decode;

/// <summary>
/// 属性特征库：由 <c>tools/convert-lang.mjs</c> 从 CrystalDiskInfo 的语言包生成。
/// 结构 = 通用表 + NVMe 表 + 厂商覆盖节；命中厂商时按 ID 覆盖通用表（与 CDI 的行为一致）。
/// </summary>
public sealed class SmartAttributeDb
{
    public sealed record VendorSection(string Id, IReadOnlyList<string> Match, IReadOnlyDictionary<string, string> Attributes);

    private readonly Dictionary<string, string> _generic;
    private readonly Dictionary<string, string> _nvme;
    private readonly List<VendorSection> _vendors;

    public string Source { get; }
    public int VendorCount => _vendors.Count;

    private SmartAttributeDb(string source, Dictionary<string, string> generic,
        Dictionary<string, string> nvme, List<VendorSection> vendors)
    {
        Source = source; _generic = generic; _nvme = nvme; _vendors = vendors;
    }

    public static SmartAttributeDb? Load(string? jsonPath = null)
    {
        string path = jsonPath ?? Path.Combine(Core.App.AppPaths.FindProductData() ?? "", "smart-attributes.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var generic = ReadTable(root, "generic");
            var nvme = ReadTable(root, "nvme");
            var vendors = new List<VendorSection>();
            if (root.TryGetProperty("vendors", out var vs) && vs.ValueKind == JsonValueKind.Array)
                foreach (var v in vs.EnumerateArray())
                {
                    string id = Str(v, "id");
                    var match = new List<string>();
                    if (v.TryGetProperty("match", out var ms) && ms.ValueKind == JsonValueKind.Array)
                        foreach (var m in ms.EnumerateArray()) match.Add((m.GetString() ?? "").ToUpperInvariant());
                    vendors.Add(new VendorSection(id, match, ReadTable(v, "attributes")));
                }
            return new SmartAttributeDb(Str(root, "source"), generic, nvme, vendors);
        }
        catch { return null; }
    }

    private static Dictionary<string, string> ReadTable(JsonElement parent, string name)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (parent.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.Object)
            foreach (var p in t.EnumerateObject()) d[p.Name] = p.Value.GetString() ?? "";
        return d;
    }

    private static string Str(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    /// <summary>按型号找厂商节（命中第一个匹配）。</summary>
    public VendorSection? MatchVendor(string model)
    {
        string m = (model ?? "").ToUpperInvariant();
        if (m.Length == 0) return null;
        foreach (var v in _vendors)
            foreach (var key in v.Match)
                if (key.Length > 0 && m.Contains(key, StringComparison.Ordinal)) return v;
        return null;
    }

    /// <summary>
    /// 解析一个属性 ID 的名字。NVMe 用 NVMe 表；ATA 用 通用表 + 厂商覆盖。
    /// </summary>
    public (string Name, string? VendorSection) Resolve(byte id, string model, bool isNvme)
    {
        string key = id.ToString("X2");
        if (isNvme)
            return (_nvme.TryGetValue(key, out var n) ? n : "厂商定义 (0x" + key + ")", null);

        var v = MatchVendor(model);
        if (v is not null && v.Attributes.TryGetValue(key, out var vn))
            return (vn, v.Id);
        return (_generic.TryGetValue(key, out var g) ? g : "厂商定义 (0x" + key + ")", v?.Id);
    }

    /// <summary>把一个属性 ID 的中文名套到属性行上（找不到就保留原值）。</summary>
    public int ApplyNames(IList<Smart.SmartAttribute> attributes, string model, bool isNvme)
    {
        int changed = 0;
        for (int i = 0; i < attributes.Count; i++)
        {
            var a = attributes[i];
            var (name, _) = Resolve(a.Id, model, isNvme);
            if (name.Length > 0 && !name.StartsWith("厂商定义", StringComparison.Ordinal))
            {
                attributes[i] = a with { NameCn = name };
                changed++;
            }
        }
        return changed;
    }
}
