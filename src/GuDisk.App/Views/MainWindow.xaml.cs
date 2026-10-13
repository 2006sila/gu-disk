using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GuDisk.App.ViewModels;
using GuDisk.Core;

namespace GuDisk.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly Services.TrayService _tray;
    private TempOverlayWindow? _overlay;
    private string _tab = "overview";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;

        _tray = new Services.TrayService(this);
        _tray.OnToggleOverlay = ToggleOverlay;
        if (Core.App.AppSettings.Current.ShowTempOverlay) Loaded += (_, _) => ShowOverlay();

        _vm.AlertRaised += a => Dispatcher.Invoke(() =>
        {
            _tray.Notify(a);
            _tray.UpdateTooltip(a.Title + " · " + a.Model);
        });
        Closing += (_, e) =>
        {
            if (!_tray.MinimizeToTray) return;
            e.Cancel = true;
            _tray.Minimize();
        };

        // 主题变化后要重设"命令式"赋值的画刷（DynamicResource 的会自动跟）
        Services.ThemeService.Instance.Changed += () =>
        {
            AdminDot.Fill = (Brush)FindResource(_vm.IsAdmin ? "Brush.Ok" : "Brush.Warn");
            RefreshPanes();
        };

        // 偏好（温度单位 / 属性名语言 / 自动刷新）变了要重建一次页面
        // 概览页的"快速跳转"卡片 → 切页签
        OverviewPane.NavigateRequested += tag =>
        {
            _tab = tag;
            foreach (var rb in Tabs.Children.OfType<RadioButton>())
                if ((string?)rb.Tag == tag) { rb.IsChecked = true; break; }
            RefreshPanes();
        };

        SettingsPane.OverlayToggleRequested += ToggleOverlay;   // 显式订阅，不留"没人接"的事件
        SettingsPane.SettingsChanged += () =>
        {
            _vm.ApplyAutoRefresh();
            _vm.ApplyTempRefresh();
            RefreshPanes();
        };

        AdminText.Text = _vm.AdminText;
        AdminDot.Fill = (Brush)FindResource(_vm.IsAdmin ? "Brush.Ok" : "Brush.Warn");
        Loaded += (_, _) => { RefreshPanes(); _ = _vm.RefreshTemperatureAsync(); };
    }

    private void OnVmPropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        // 温度每 N 秒变一次：只在概览页重绘，避免在 S.M.A.R.T. 页把表格重设 ItemsSource（会丢滚动位置）
        if (e.PropertyName == nameof(MainViewModel.TemperatureCelsius))
        {
            if (_tab == "overview") Dispatcher.Invoke(RefreshPanes);
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.Diagnostic) or nameof(MainViewModel.IsBusy)
            or nameof(MainViewModel.SelectedDrive))
            Dispatcher.Invoke(() =>
            {
                RefreshPanes();
                if (_overlay is { IsVisible: true }) _overlay.Track(_vm.Drives.ToList(), _vm.SelectedDrive);
            });
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        _tab = tag;
        RefreshPanes();
    }

    private void RefreshPanes()
    {
        // XAML 里的 IsChecked="True" 会在 InitializeComponent() 期间就触发 Checked，
        // 那时具名控件字段还没赋值 —— 必须守卫，否则构造函数里直接 NRE 崩溃。
        if (OverviewPane is null || SmartPane is null || SelfTestPane is null || BenchPane is null || ScanPane is null
            || ReportPane is null || SettingsPane is null || PlaceholderPane is null
            || BusyPane is null || EngineText is null) return;

        var d = _vm.Diagnostic;
        bool have = d is not null;

        bool needDrive = _tab != "settings";
        bool showPane = have || !needDrive;

        OverviewPane.Visibility = _tab == "overview" && have ? Visibility.Visible : Visibility.Collapsed;
        SmartPane.Visibility = _tab == "smart" && have ? Visibility.Visible : Visibility.Collapsed;
        SelfTestPane.Visibility = _tab == "selftest" && have ? Visibility.Visible : Visibility.Collapsed;
        BenchPane.Visibility = _tab == "bench" && have ? Visibility.Visible : Visibility.Collapsed;
        ScanPane.Visibility = _tab == "scan" && have ? Visibility.Visible : Visibility.Collapsed;
        ReportPane.Visibility = _tab == "report" && have ? Visibility.Visible : Visibility.Collapsed;
        SettingsPane.Visibility = _tab == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPane.Visibility = showPane ? Visibility.Collapsed : Visibility.Visible;
        BusyPane.Visibility = Visibility.Collapsed;

        if (!have)
        {
            PlaceholderTitle.Text = "尚未取得诊断数据";
            PlaceholderBody.Text = _vm.SelectedDrive is null ? "没有检测到物理磁盘" : "正在读取 " + _vm.SelectedDrive.Model;
            PlaceholderHint.Text = _vm.StatusText;
            return;
        }

        string status = d!.Drive.Model + " · " + d.Verdict.Label + " · " + d.Verdict.CountsText;
        switch (_tab)
        {
            case "overview": OverviewPane.Show(_vm); break;
            case "smart": SmartPane.Show(_vm.Attributes.ToList(), d.Smart.Nvme is not null, d.Smart.Engine); break;
            case "selftest": SelfTestPane.Show(d.Drive); break;
            case "bench": BenchPane.Show(d.Drive, status); break;
            case "scan": ScanPane.Show(d.Drive, status); break;
            case "report": ReportPane.Show(d.Drive, d, status); break;
            case "settings": SettingsPane.Show(_vm.RecentAlerts.ToList(), _vm.Drives.ToList()); break;
        }

        EngineText.Text = have
            ? "引擎 " + d!.Smart.Engine + (d.CrossCheck.Count > 0 ? " · 交叉校验 " + d.CrossCheck.Count + " 项" : "")
            : "";
    }

    /// <summary>切换桌面温度悬浮窗（托盘菜单与设置页共用入口）。</summary>
    public void ToggleOverlay()
    {
        if (_overlay is { IsVisible: true }) { _overlay.Hide(); Core.App.AppSettings.Current.ShowTempOverlay = false; }
        else ShowOverlay();
        Core.App.AppSettings.Current.Save();
    }

    private void ShowOverlay()
    {
        _overlay ??= new TempOverlayWindow();
        _overlay.Show();
        _overlay.Track(_vm.Drives.ToList(), _vm.SelectedDrive);
        Core.App.AppSettings.Current.ShowTempOverlay = true;
        Core.App.AppSettings.Current.Save();
    }

    private void OnRescan(object sender, RoutedEventArgs e) => _vm.Scan();

    private void OnToggleTheme(object sender, RoutedEventArgs e) => Services.ThemeService.Instance.Toggle();
}
