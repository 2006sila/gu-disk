namespace GuDisk.Core.App;

/// <summary>
/// 数据根判定（便携优先）：
///   GUDISK_HOME 环境变量 &gt; exe 同级 data\（可写探测通过）&gt; %LOCALAPPDATA%\Gu-disk
/// 随包资源（smartctl / 特征库）另做"开发态向上回溯"，否则 bin\ 下跑自检永远找不到。
/// </summary>
public static class AppPaths
{
    public const string HomeEnvVar = "GUDISK_HOME";
    public const string PortableDirName = "data";
    public const string FallbackDirName = "Gu-disk";
    private const int MaxWalkUp = 7;

    private static readonly Lazy<string> _root = new(Resolve);
    public static string Root => _root.Value;

    public static string History   => Path.Combine(Root, "history");
    public static string Bench     => Path.Combine(Root, "bench");
    public static string Scan      => Path.Combine(Root, "scan");
    public static string Logs      => Path.Combine(Root, "logs");
    public static string Settings  => Path.Combine(Root, "settings.json");
    public static string Alerts    => Path.Combine(Root, "alerts.jsonl");

    /// <summary>产品数据目录的默认位置（发布态：与 exe 同级）。</summary>
    public static string ProductData => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>smartctl 目录的默认位置（发布态：与 exe 同级）。</summary>
    public static string SmartctlDir => Path.Combine(AppContext.BaseDirectory, "tools", "smartctl");

    /// <summary>实际可用的产品数据目录：先看发布布局，再向上回溯找仓库布局。</summary>
    public static string? FindProductData()
        => WalkUp("data", f => File.Exists(Path.Combine(f, "smart-attributes.json")))
           ?? (Directory.Exists(ProductData) ? ProductData : null);

    /// <summary>实际可用的 smartctl 目录。</summary>
    public static string? FindSmartctlDir()
        => WalkUp(Path.Combine("tools", "smartctl"),
                  f => File.Exists(Path.Combine(f, "smartctl.exe")));

    /// <summary>开发期便利：向上找参照软件里的 CDI 历史目录（发布包不会带它）。</summary>
    public static string? FindCdiSmartFolder()
        => WalkUp(Path.Combine("参考软件", "CrystalDiskInfo", "Smart"), Directory.Exists);

    private static string? WalkUp(string relative, Func<string, bool> probe)
    {
        var direct = Path.Combine(AppContext.BaseDirectory, relative);
        if (probe(direct)) return direct;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < MaxWalkUp && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (probe(candidate)) return candidate;
        }
        return null;
    }

    public static void EnsureCreated()
    {
        foreach (var d in new[] { Root, History, Bench, Scan, Logs })
            Directory.CreateDirectory(d);
    }

    internal static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable(HomeEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
            return Path.GetFullPath(env);

        var portable = Path.Combine(AppContext.BaseDirectory, PortableDirName);
        if (IsWritable(portable)) return portable;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            FallbackDirName);
    }

    internal static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>在向上若干层的 tools/ 里找指定文件（开发期与发布后都能找到）。</summary>
    public static string? FindTool(string fileName)
    {
        var dir = new DirectoryInfo(Root);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "tools", fileName);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
