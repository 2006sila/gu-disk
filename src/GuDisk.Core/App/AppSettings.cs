using System.Text.Json;

namespace GuDisk.Core.App;

/// <summary>
/// 界面偏好的持久化（`data/app-settings.json`）。
/// 只存"用户选择"，不存任何设备数据 —— 设备数据走历史库。
/// </summary>
public sealed class AppSettings
{
    /// <summary>温度是否用华氏度显示。</summary>
    public bool Fahrenheit { get; set; }

    /// <summary>属性名是否用英文（对应 CDI 的 SMART_IN_ENGLISH）。</summary>
    public bool EnglishNames { get; set; }

    /// <summary>自动刷新间隔（分钟）。**旧字段**，仅用于读取老配置文件。</summary>
    public int AutoRefreshMinutes { get; set; }

    /// <summary>自动刷新间隔（秒），0 = 关闭。比分钟灵活，支持自定义到秒级。</summary>
    public int AutoRefreshSeconds { get; set; }

    /// <summary>
    /// 实际生效的刷新间隔（秒）：新字段优先，为 0 时按旧字段的分钟换算。
    /// **标 JsonIgnore** —— 计算属性写进配置文件会让人以为改它有用（曾经就写进去过）。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveRefreshSeconds => AutoRefreshSeconds > 0 ? AutoRefreshSeconds : AutoRefreshMinutes * 60;

    /// <summary>
    /// 温度单独一路的刷新间隔（秒），与"整体诊断刷新"解耦。
    /// 一次温度读取只用原生引擎（NVMe 免管理员，约 20 ms），比全量诊断（双引擎 + 交叉校验，数百毫秒）轻两个数量级，
    /// 所以可以设得更密。
    /// </summary>
    public int TempRefreshSeconds { get; set; } = 10;

    /// <summary>是否使用浅色主题。</summary>
    public bool LightTheme { get; set; }

    /// <summary>温度告警注意阈值（℃）。</summary>
    public double TempWarnC { get; set; } = 55;

    /// <summary>温度告警危险阈值（℃）。</summary>
    public double TempCritC { get; set; } = 65;

    /// <summary>告警时是否播放系统提示音。</summary>
    public bool AlertSound { get; set; } = true;

    /// <summary>悬浮窗位置（null = 首选用右下角默认位）。</summary>
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }

    /// <summary>
    /// 温度悬浮窗显示几块盘：1 = 只显示当前选中的盘；N = 最多 N 块（当前盘优先）；0 = 全部。
    /// 仅在 <see cref="OverlayDriveKeys"/> 为空时生效。
    /// </summary>
    public int OverlayDriveCount { get; set; } = 1;

    /// <summary>
    /// 逐盘勾选：要显示的盘（存 DiskKey = SHA256(型号|序列号)[..16]，**不能用设备号** —— 热插拔会漂移）。
    /// 非空时以它为准，空则回落到 OverlayDriveCount 的数量规则。
    /// </summary>
    public List<string> OverlayDriveKeys { get; set; } = [];

    /// <summary>是否显示桌面温度悬浮窗。</summary>
    public bool ShowTempOverlay { get; set; }

    /// <summary>smartctl 的 -d 参数强制覆盖（空 = 自动）。硬盘盒兼容用。</summary>
    public string BridgeDeviceType { get; set; } = "";

    /// <summary>
    /// RAID 卡的 smartctl -d 覆盖（如 megaraid,0 / aacraid,0,0,0 / 3ware,0 / areca,1 / cciss,0 / sat+raid）。
    ///
    /// 为什么需要：RAID 卡后面的成员盘被控制器驱动占住，Windows 根本枚举不到它们，
    /// 只能靠 smartctl 走控制器私有通道读。smartmontools 内置了这些协议，我们只要把 -d 传对。
    /// </summary>
    public string RaidDeviceType { get; set; } = "";

    /// <summary>强制指定的 -d：**RAID 模式优先**（在 RAID 卡上再谈桥接模式没有意义）。</summary>
    public string DeviceTypeOverride =>
        !string.IsNullOrWhiteSpace(RaidDeviceType) ? RaidDeviceType :
        !string.IsNullOrWhiteSpace(BridgeDeviceType) ? BridgeDeviceType : "";

    /// <summary>折算成 AlertEvaluator 用的规则。</summary>
    public AlertRule ToRule() => new(TempWarnC, TempCritC);   // AlertRule 就在本命名空间

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = Load();

    public static AppSettings Load()
    {
        try
        {
            string p = PathOf();
            if (!File.Exists(p)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(p)) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(PathOf(), JsonSerializer.Serialize(this, Opts));
        }
        catch { /* 存不下也不该让界面崩 */ }
    }

    private static string PathOf() => Path.Combine(AppPaths.Root, "app-settings.json");
}
