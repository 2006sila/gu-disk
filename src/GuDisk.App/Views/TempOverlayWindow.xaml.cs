using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GuDisk.App.ViewModels;
using GuDisk.Core.App;
using GuDisk.Core.Decode;
using GuDisk.Core.Devices;
using GuDisk.Core.Smart;

namespace GuDisk.App.Views;

/// <summary>
/// 桌面温度悬浮窗（CDI 的 SHOW_TEMPERATURE_ICON 对应功能）。
///
/// 轻量：首选**原生引擎**读健康日志（NVMe 免管理员、约 20 ms），所以可以设得比整体诊断密得多。
/// **原生失败时兜底走 smartctl** —— USB-NVMe 桥接盘的原生引擎已被跳过（发 ATA 命令必然 Win32 87），
/// 不兜底的话悬浮窗温度永远是 "--"。smartctl 慢（约 280 ms），但温度刷新 10 秒一次完全可接受。
/// 支持显示多块盘：数量由设置里的"悬浮窗显示"决定（当前盘优先）。
/// </summary>
public partial class TempOverlayWindow : Window
{
    public sealed record Row(string Name, string FullName, string Temp, Brush Brush);

    private readonly DispatcherTimer _timer;
    private IReadOnlyList<PhysicalDriveInfo> _drives = [];
    private PhysicalDriveInfo? _selected;
    private bool _busy;

    public TempOverlayWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = Interval() };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { RestorePosition(); _timer.Start(); Refresh(); };
        Closed += (_, _) => _timer.Stop();
    }

    private static TimeSpan Interval()
        => TimeSpan.FromSeconds(Math.Clamp(AppSettings.Current.TempRefreshSeconds, 1, 3600));

    /// <summary>更新要监控的盘列表（宿主窗口在设备变化 / 切换页签时调用）。</summary>
    public void Track(IReadOnlyList<PhysicalDriveInfo> drives, PhysicalDriveInfo? selected)
    {
        _drives = drives;
        if (selected is not null) _selected = selected;
        Refresh();
    }

    private async void Refresh()
    {
        if (_drives.Count == 0) return;
        if (_busy) return;

        // 每跳都重读间隔：改了设置不用重建窗口
        if (Math.Abs(_timer.Interval.TotalSeconds - Interval().TotalSeconds) > 0.5)
            _timer.Interval = Interval();

        _busy = true;
        try
        {
            var list = Pick();
            // 多块盘并行读。原生快（约 20 ms）先试；失败再走 smartctl（约 280 ms）——
            // **只有原生失败才兜底**，不白花那 280 ms。
            var engine = new SmartctlEngine();
            var results = await Task.WhenAll(list.Select(d => Task.Run(() =>
            {
                var n = SmartProbe.Read(d, "auto");
                double? temp = n.Nvme?.CompositeTemperatureC;
                if (temp is null && !n.Success)
                    temp = engine.Read(d).Parsed?.Nvme?.CompositeTemperatureC;
                return (Drive: d, Temp: temp);
            })));
            if (!IsLoaded) return;

            double warn = AppSettings.Current.TempWarnC, crit = AppSettings.Current.TempCritC;
            Brush Pick2(double? t) => (Brush)FindResource(
                t is null ? "Brush.Text3" : t >= crit ? "Brush.Bad" : t >= warn ? "Brush.Warn" : "Brush.Lime");
            string Text(double? t) => t is null ? "--" : UnitConverter.FormatTemp(t.Value, AppSettings.Current.Fahrenheit);

            if (results.Length == 1)
            {
                // 单盘：保持大号显示
                SinglePanel.Visibility = Visibility.Visible;
                MultiList.Visibility = Visibility.Collapsed;
                var r = results[0];
                TempText.Text = Text(r.Temp);
                TempText.Foreground = Pick2(r.Temp);
                ModelText.Text = r.Drive.Model;
            }
            else
            {
                SinglePanel.Visibility = Visibility.Collapsed;
                MultiList.Visibility = Visibility.Visible;
                MultiList.ItemsSource = results
                    .Select(r => new Row(r.Drive.Model, r.Drive.Model + " · " + r.Drive.Serial,
                                         Text(r.Temp), Pick2(r.Temp)))
                    .ToList();
            }
        }
        catch { /* 读不到就保持原样 */ }
        finally { _busy = false; }
    }

    /// <summary>按设置挑要显示的盘：先看逐盘勾选，没勾才回落到数量规则（当前盘优先）。</summary>
    private List<PhysicalDriveInfo> Pick()
    {
        // ① 逐盘勾选优先：按勾选顺序取，找不到的（换过盘）自动跳过
        var keys = AppSettings.Current.OverlayDriveKeys;
        if (keys.Count > 0)
        {
            var picked = keys.Select(k => _drives.FirstOrDefault(d => d.DiskKey == k))
                             .Where(d => d is not null).Select(d => d!).ToList();
            if (picked.Count > 0) return picked;
        }

        // ② 数量规则
        int want = AppSettings.Current.OverlayDriveCount;
        var all = _drives;
        if (want <= 0 || want >= all.Count) return all.ToList();

        var list = new List<PhysicalDriveInfo>();
        if (_selected is not null && all.Contains(_selected)) list.Add(_selected);
        foreach (var d in all)
        {
            if (list.Count >= want) break;
            if (!list.Contains(d)) list.Add(d);
        }
        return list;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); SavePosition(); } catch { }
    }

    private void OnMenu(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "显示主窗口", Command = new RelayCommand(_ => ShowMain()) });
        menu.Items.Add(new MenuItem { Header = "立即刷新", Command = new RelayCommand(_ => Refresh()) });
        menu.Items.Add(new MenuItem { Header = "隐藏悬浮窗", Command = new RelayCommand(_ => Hide()) });
        menu.IsOpen = true;
    }

    private void ShowMain()
    {
        if (Application.Current.MainWindow is { } w) { w.Show(); w.WindowState = WindowState.Normal; w.Activate(); }
    }

    private void SavePosition()
    {
        AppSettings.Current.OverlayLeft = Left;
        AppSettings.Current.OverlayTop = Top;
        AppSettings.Current.Save();
    }

    private void RestorePosition()
    {
        if (AppSettings.Current.OverlayLeft is { } l && AppSettings.Current.OverlayTop is { } t)
        {
            Left = l; Top = t;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - 200; Top = wa.Top + 40;
        }
    }
}
