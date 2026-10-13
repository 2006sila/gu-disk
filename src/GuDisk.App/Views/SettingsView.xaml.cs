using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GuDisk.Core.Devices;
using System.Windows.Threading;
using GuDisk.Core;
using GuDisk.Core.App;
using GuDisk.Core.Decode;
using GuDisk.Core.History;

namespace GuDisk.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>
    /// 构造期间必须为 true：XAML 里 `IsSelected="True"` 会在 InitializeComponent() 阶段
    /// 触发 SelectionChanged，那时**后面才声明的具名控件还是 null**（BridgeText 就因此 NRE 崩过）。
    /// 与 RadioButton 的 IsChecked 是同一类坑。
    /// </summary>
    private bool _loading = true;

    /// <summary>悬浮窗逐盘勾选的一行（Checked 需要可写 setter，TwoWay 绑定才回得来）。</summary>
    public sealed class PickRow
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public bool Checked { get; set; }
    }

    public void Show(IReadOnlyList<AlertItem> recentAlerts, IReadOnlyList<PhysicalDriveInfo>? drives = null)
    {
        FillOverlayDrives(drives ?? []);
        ShowCore(recentAlerts);
    }

    private void ShowCore(IReadOnlyList<AlertItem> recentAlerts)
    {
        // 载入偏好（回填控件时不触发保存）
        _loading = true;
        TempUnitBox.SelectedIndex = AppSettings.Current.Fahrenheit ? 1 : 0;
        NameLangBox.SelectedIndex = AppSettings.Current.EnglishNames ? 1 : 0;
        FillRefreshUi();
        FillTempRefreshUi();
        _loading = false;

        TempWarnBox.Text = AppSettings.Current.TempWarnC.ToString("0.#");
        TempCritBox.Text = AppSettings.Current.TempCritC.ToString("0.#");
        SoundBox.IsChecked = AppSettings.Current.AlertSound;
        OverlayBox.IsChecked = AppSettings.Current.ShowTempOverlay;
        OverlayCountBox.SelectedIndex = AppSettings.Current.OverlayDriveCount switch
        {
            2 => 1, 3 => 2, 4 => 3, <= 0 => 4, _ => 0
        };
        UpdateOverlayHint();
        int raidIdx = Array.IndexOf(RaidValues, AppSettings.Current.RaidDeviceType);
        RaidBox.SelectedIndex = raidIdx >= 0 ? raidIdx : 0;
        UpdateRaidHint();
        BridgeBox.SelectedIndex = AppSettings.Current.BridgeDeviceType switch
        {
            "sat" => 1, "usb" => 2, "nvme" => 3, "sntrealtek" => 4, "sntjmicron" => 5,
            "usbjmicron" => 6, "usbasmedia" => 7, "scsi" => 8, "ata" => 9, _ => 0
        };
        UpdateAlertRuleText();
        RefreshHistory();

        ElevateBtn.Visibility = AdminHelper.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
        ToolText.Text = AdminHelper.IsAdmin
            ? "当前已是管理员，SMART / 自检 / 裸盘扫描全部可用。"
            : "当前受限：SMART 直通、自检、裸盘扫描需要管理员。点左侧按钮可一键提权重启。";
        PathList.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("数据根", AppPaths.Root),
            new("历史目录", AppPaths.History),
            new("日志目录", AppPaths.Logs),
            new("产品数据", AppPaths.FindProductData() ?? "（未找到）"),
            new("smartctl", AppPaths.FindSmartctlDir() ?? "（未找到）"),
        };

        var attrDb = SmartAttributeDb.Load();
        var drivedb = DrivedbIndex.Load();
        AboutList.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("产品", GuDiskInfo.ProductName + " " + GuDiskInfo.Version + "（" + GuDiskInfo.Stage + "）"),
            new("运行时", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription),
            new("属性特征库", attrDb is null ? "未加载" : attrDb.Source + " · " + attrDb.VendorCount + " 个厂商节"),
            new("驱动库", drivedb is null ? "未加载" : drivedb.Source + " · " + drivedb.Total + " 型号（SMR " + drivedb.SmrCount + "）"),
            new("引擎", "原生 IOCTL（免管理员可读 NVMe） + smartctl 兜底与交叉校验"),
            new("驱动安装", "无 —— 纯用户态 DeviceIoControl，不装任何内核驱动"),
        };

        ThemeText.Text = Services.ThemeService.Instance.Label + "　（点左侧按钮切换）";

        // 告警历史改从磁盘读（RefreshHistory），这里不再用内存列表
    }

    /// <summary>请求切换桌面温度悬浮窗（宿主窗口显式订阅，避免"事件没人接"的老问题）。</summary>
    public event Action? OverlayToggleRequested;

    private static readonly string[] BridgeValues =
        ["", "sat", "usb", "nvme", "sntrealtek", "sntjmicron", "usbjmicron", "usbasmedia", "scsi", "ata"];

    private void UpdateAlertRuleText()
    {
        var r = AppSettings.Current;
        AlertRuleText.Text = "当前规则：温度 ≥ " + r.TempWarnC.ToString("0.#") + " ℃ 注意，≥ "
            + r.TempCritC.ToString("0.#") + " ℃ 危险；状态档位变化触发；告警会写入磁盘并在设置页留档。"
            + (r.AlertSound ? "提示音已开。" : "提示音已关。");
    }

    private void OnApplyThresholds(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(TempWarnBox.Text, out var warn) && warn is > 0 and < 120)
            AppSettings.Current.TempWarnC = warn;
        if (double.TryParse(TempCritBox.Text, out var crit) && crit is > 0 and < 120)
            AppSettings.Current.TempCritC = crit;
        if (AppSettings.Current.TempCritC <= AppSettings.Current.TempWarnC)
            AppSettings.Current.TempCritC = AppSettings.Current.TempWarnC + 10;

        TempWarnBox.Text = AppSettings.Current.TempWarnC.ToString("0.#");
        TempCritBox.Text = AppSettings.Current.TempCritC.ToString("0.#");
        UpdateAlertRuleText();
        Save();
    }

    private void OnSoundChanged(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.AlertSound = SoundBox.IsChecked == true;
        UpdateAlertRuleText();
        Save();
    }

    private string _overlayListSignature = "";

    private void FillOverlayDrives(IReadOnlyList<PhysicalDriveInfo> drives)
    {
        // RAID 卷：Windows 只能枚举到阵列这一层，成员盘读不到，在这里直说
        var raids = new List<PhysicalDriveInfo>();
        foreach (var dd in drives)
        {
            if (dd.Bus == BusKind.Raid) raids.Add(dd);
        }
        if (raids.Count > 0 && RaidHint is not null)
        {
            var names = string.Join("、", raids.ConvertAll(x => x.Model));
            RaidHint.Text += " 检测到 " + raids.Count + " 个 RAID 卷(" + names + ")：成员盘的 SMART 需在此指定控制器模式";
        }
        // 盘集合没变就不重建：任何一次 Show() 都重建的话，会把正在勾的列表打断
        string sig = string.Join(",", drives.Select(d => d.DiskKey));
        var keys = AppSettings.Current.OverlayDriveKeys;
        if (sig == _overlayListSignature && OverlayDriveList.ItemsSource is not null)
        {
            foreach (var r in (List<PickRow>)OverlayDriveList.ItemsSource)
                r.Checked = keys.Contains(r.Key);
            UpdateOverlayHint();
            return;
        }
        _overlayListSignature = sig;
        OverlayDriveList.ItemsSource = drives.Select(d => new PickRow
        {
            Key = d.DiskKey,
            Label = d.Model + (d.MountPoints.Count > 0 ? "（" + string.Join(" ", d.MountPoints) + "）" : ""),
            Checked = keys.Contains(d.DiskKey),
        }).ToList();
        UpdateOverlayHint();
    }

    private void UpdateOverlayHint()
    {
        var rows = (OverlayDriveList.ItemsSource as List<PickRow>) ?? [];
        int n = rows.Count(r => r.Checked);
        OverlayHint.Text = n > 0
            ? "当前：按勾选显示 " + n + " 块盘（" + string.Join("、", rows.Where(r => r.Checked).Select(r => r.Label)) + "）"
            : "当前：未勾选任何盘 → 按上面的数量规则显示" + (OverlayDriveList.ItemsSource is null ? "（尚未识别到磁盘）" : "");
    }

    private void OnOverlayDriveToggled(object sender, RoutedEventArgs e)
    {
        // 这里**不能**用 _loading 守卫：Click 只可能由用户触发，构造期不会走到这里，
        // 而 _loading 在 Show() 里为真会把用户的勾选整个吞掉（实测勾上了但没保存）。
        var rows = (OverlayDriveList.ItemsSource as List<PickRow>) ?? [];
        AppSettings.Current.OverlayDriveKeys = rows.Where(r => r.Checked).Select(r => r.Key).ToList();
        UpdateOverlayHint();
        Save();
    }

    private void OnOverlayCountChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.OverlayDriveCount = OverlayCountBox.SelectedIndex switch
        {
            1 => 2, 2 => 3, 3 => 4, 4 => 0, _ => 1
        };
        Save();
    }

    private void OnOverlayChanged(object sender, RoutedEventArgs e)
    {
        OverlayToggleRequested?.Invoke();
        OverlayBox.IsChecked = AppSettings.Current.ShowTempOverlay;
        Save();
    }

    private static readonly string[] RaidValues =
        ["", "megaraid,0", "megaraid,1", "aacraid,0,0,0", "3ware,0", "areca,1", "cciss,0", "sat+raid"];

    private void OnRaidChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RaidHint is null) return;
        int i = Math.Clamp(RaidBox.SelectedIndex, 0, RaidValues.Length - 1);
        AppSettings.Current.RaidDeviceType = RaidValues[i];
        UpdateRaidHint();
        Save();
    }

    /// <summary>检测到 RAID 卷时把话说到位 —— 否则用户只会看到"少了几块盘"。</summary>
    private void UpdateRaidHint()
    {
        string cur = AppSettings.Current.RaidDeviceType;
        string tail = cur.Length == 0
            ? "当前：自动（smartctl 自己判断）"
            : "当前：强制 -d " + cur;
        RaidHint.Text = tail;
    }

    private void OnBridgeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BridgeText is null) return;
        int i = Math.Clamp(BridgeBox.SelectedIndex, 0, BridgeValues.Length - 1);
        AppSettings.Current.BridgeDeviceType = BridgeValues[i];
        BridgeText.Text = BridgeValues[i].Length == 0
            ? "当前：自动（按桥接芯片识别结果决定）"
            : "当前：强制 -d " + BridgeValues[i];
        Save();
    }

    private void OnRefreshHistory(object sender, RoutedEventArgs e) => RefreshHistory();

    private void RefreshHistory()
    {
        var store = new GuDisk.Core.History.AlertStore();
        var items = store.Recent(30);
        AlertHistoryList.ItemsSource = items.Count > 0 ? items : null;
        AlertHistoryHeader.Text = "告警历史（累计 " + store.Count() + " 条，显示最近 " + items.Count + "）";
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        new GuDisk.Core.History.AlertStore().Clear();
        RefreshHistory();
    }

    /// <summary>在线更新 drivedb（下载 + 调 Node 转换脚本重建索引）。</summary>
    private async void OnUpdateDb(object sender, RoutedEventArgs e)
    {
        DbBtn.IsEnabled = false;
        ToolText.Text = "正在检查下载源…";
        try
        {
            var probe = await GuDisk.Core.Decode.DrivedbUpdater.ProbeAsync();
            if (!probe.Success)
            {
                ToolText.Text = "更新失败：" + probe.Error
                    + "。可以手动下载 drivedb.h 覆盖 tools/smartctl 下的同名文件，再跑 node tools/convert-drivedb.mjs。";
                return;
            }
            ToolText.Text = "正在从 " + new Uri(probe.Url!).Host + " 下载…";
            var r = await GuDisk.Core.Decode.DrivedbUpdater.UpdateAsync(m => Dispatcher.Invoke(() => ToolText.Text = m));
            ToolText.Text = r.Success
                ? "已更新到 " + r.Version + "（" + r.Bytes + " 字节）；索引重建："
                  + (r.IndexRebuilt ? "完成，重启程序后生效" : "未完成 —— " + r.ConverterOutput)
                : "更新失败：" + r.Error;
        }
        catch (Exception ex) { ToolText.Text = "更新异常：" + ex.Message; }
        finally { DbBtn.IsEnabled = true; }
    }

    private void OnTempUnitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.Fahrenheit = TempUnitBox.SelectedIndex == 1;
        Save();
    }

    private void OnNameLangChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.EnglishNames = NameLangBox.SelectedIndex == 1;
        Save();
    }

    /// <summary>预设档位（秒）；索引 9 是"自定义"，用输入框里的值。</summary>
    private static readonly int[] RefreshPresets = [0, 5, 10, 30, 60, 300, 900, 1800, 3600];

    private const int CustomIndex = 9;
    private const int MinRefreshSeconds = 5;          // 低于 5 秒没意义：一次 SMART 读取就要几十毫秒
    private const int MaxRefreshSeconds = 24 * 3600;

    /// <summary>把当前设置回填到界面上（预设命中就用预设，否则落到"自定义"）。</summary>
    private void FillRefreshUi()
    {
        int sec = AppSettings.Current.EffectiveRefreshSeconds;
        int idx = Array.IndexOf(RefreshPresets, sec);
        if (idx >= 0)
        {
            AutoRefreshBox.SelectedIndex = idx;
            SetCustomEnabled(false);
        }
        else
        {
            AutoRefreshBox.SelectedIndex = CustomIndex;
            // 回填成最合适的单位，避免"7200 秒"这种难读的数
            if (sec > 0 && sec % 3600 == 0) { CustomRefreshBox.Text = (sec / 3600).ToString(); CustomUnitBox.SelectedIndex = 2; }
            else if (sec > 0 && sec % 60 == 0) { CustomRefreshBox.Text = (sec / 60).ToString(); CustomUnitBox.SelectedIndex = 1; }
            else { CustomRefreshBox.Text = sec > 0 ? sec.ToString() : "30"; CustomUnitBox.SelectedIndex = 0; }
            SetCustomEnabled(true);
        }
        UpdateRefreshHint();
    }

    private void SetCustomEnabled(bool on)
    {
        CustomRefreshBox.IsEnabled = on;
        CustomUnitBox.IsEnabled = on;
        CustomApplyBtn.IsEnabled = on;
    }

    private void UpdateRefreshHint()
    {
        int sec = AppSettings.Current.EffectiveRefreshSeconds;
        int tsec = Math.Clamp(AppSettings.Current.TempRefreshSeconds, 1, 3600);
        RefreshHint.Text = (sec <= 0
                ? "整体诊断：关闭（只手动刷新）"
                : "整体诊断：每 " + Describe(sec) + " 一次（下限 " + MinRefreshSeconds + " 秒）")
            + "　·　温度：每 " + tsec + " 秒一次"
            + "\n温度这一路只走原生引擎（约 20 ms），比整体诊断轻得多，所以可以设得更密。";
    }

    private static string Describe(int sec)
        => sec % 3600 == 0 ? (sec / 3600) + " 小时"
         : sec % 60 == 0 ? (sec / 60) + " 分钟"
         : sec + " 秒";

    private static readonly int[] TempPresets = [1, 2, 5, 10, 30, 60];
    private const int TempCustomIndex = 6;

    private void FillTempRefreshUi()
    {
        int sec = Math.Clamp(AppSettings.Current.TempRefreshSeconds, 1, 3600);
        int idx = Array.IndexOf(TempPresets, sec);
        if (idx >= 0) { TempRefreshBox.SelectedIndex = idx; SetTempCustomEnabled(false); }
        else { TempRefreshBox.SelectedIndex = TempCustomIndex; CustomTempBox.Text = sec.ToString(); SetTempCustomEnabled(true); }
        UpdateRefreshHint();
    }

    private void SetTempCustomEnabled(bool on)
    {
        CustomTempBox.IsEnabled = on;
        CustomTempBtn.IsEnabled = on;
    }

    private void OnTempRefreshChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        int i = TempRefreshBox.SelectedIndex;
        if (i == TempCustomIndex) { SetTempCustomEnabled(true); return; }
        SetTempCustomEnabled(false);
        if (i >= 0 && i < TempPresets.Length)
        {
            AppSettings.Current.TempRefreshSeconds = TempPresets[i];
            UpdateRefreshHint();
            Save();
        }
    }

    private void OnApplyCustomTemp(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CustomTempBox.Text.Trim(), out var n) || n <= 0)
        { RefreshHint.Text = "温度间隔要填正整数。"; return; }
        if (n > 3600) { RefreshHint.Text = "温度间隔最多 3600 秒。"; return; }
        AppSettings.Current.TempRefreshSeconds = n;
        UpdateRefreshHint();
        Save();
    }

    private void OnAutoRefreshChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        int i = AutoRefreshBox.SelectedIndex;
        if (i == CustomIndex)
        {
            SetCustomEnabled(true);
            RefreshHint.Text = "填数值 → 选单位 → 点「应用」";
            return;
        }
        SetCustomEnabled(false);
        if (i >= 0 && i < RefreshPresets.Length)
        {
            AppSettings.Current.AutoRefreshSeconds = RefreshPresets[i];
            AppSettings.Current.AutoRefreshMinutes = 0;   // 新值生效后清掉旧字段，避免两处打架
            UpdateRefreshHint();
            Save();
        }
    }

    private void OnApplyCustomRefresh(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CustomRefreshBox.Text.Trim(), out var n) || n <= 0)
        {
            RefreshHint.Text = "请填一个正整数。";
            return;
        }
        int mult = CustomUnitBox.SelectedIndex switch { 2 => 3600, 1 => 60, _ => 1 };
        long sec = (long)n * mult;

        if (sec < MinRefreshSeconds) { RefreshHint.Text = "太短了：最少 " + MinRefreshSeconds + " 秒。"; return; }
        if (sec > MaxRefreshSeconds) { RefreshHint.Text = "太长了：最多 24 小时。"; return; }

        AppSettings.Current.AutoRefreshSeconds = (int)sec;
        AppSettings.Current.AutoRefreshMinutes = 0;
        UpdateRefreshHint();
        Save();
    }

    /// <summary>保存偏好并通知主界面重建视图（单位/语言会改变已有文本）。</summary>
    private void Save()
    {
        AppSettings.Current.Save();
        SettingsChanged?.Invoke();
    }

    /// <summary>偏好已变更（宿主窗口据此刷新所有页面）。</summary>
    public event Action? SettingsChanged;

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (!AdminHelper.RestartElevated())
            ToolText.Text = "提权被取消（或 UAC 被拒绝）。也可以手动右键程序 → 以管理员身份运行。";
        else Application.Current.Shutdown();
    }

    private void OnDiskMgmt(object sender, RoutedEventArgs e)
    {
        if (!AdminHelper.OpenDiskManagement()) ToolText.Text = "无法启动磁盘管理（diskmgmt.msc）。";
    }

    private void OnDevMgmt(object sender, RoutedEventArgs e)
    {
        if (!AdminHelper.OpenDeviceManager()) ToolText.Text = "无法启动设备管理器（devmgmt.msc）。";
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        Services.ThemeService.Instance.Toggle();     // 唯一入口，与顶栏 ◐ 等价
        ThemeText.Text = Services.ThemeService.Instance.Label;
    }

    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppPaths.Root + "\"") { UseShellExecute = true });
    }

    private void OnImportCdi(object sender, RoutedEventArgs e)
    {
        string? root = AppPaths.FindCdiSmartFolder();
        if (root is null)
        {
            ImportText.Text = "未找到 CrystalDiskInfo 的 Smart 目录（开发期便利路径：工作区 / 参考软件 / CrystalDiskInfo / Smart）";
            return;
        }
        var r = new CdiHistoryImporter().Import(root);
        ImportText.Text = "已从 " + root + " 导入：" + r.Folders + " 个盘目录 · " + r.Files
            + " 个 CSV · 新增 " + r.Points + " 个数据点（重复导入不会重复计数）";
    }
}
