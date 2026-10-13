using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GuDisk.Core;
using GuDisk.Core.App;
using GuDisk.Core.Decode;
using GuDisk.Core.Devices;
using GuDisk.Core.Health;
using GuDisk.Core.History;
using GuDisk.Core.Smart;

namespace GuDisk.App.ViewModels;

/// <summary>极简 MVVM：不引任何框架，只实现 INotifyPropertyChanged + 一个 ICommand。</summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? p) => canExecute?.Invoke(p) ?? true;
    public void Execute(object? p) => execute(p);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>刻度条上的一格。</summary>
public sealed class TickVm(bool filled) : INotifyPropertyChanged
{
    private bool _filled = filled;
    public bool Filled { get => _filled; set { _filled = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>主界面 ViewModel：设备列表 + 当前诊断结果 + 状态。</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    /// <summary>界面偏好（持久化）。</summary>
    public AppSettings Settings { get; } = AppSettings.Current;

    private DispatcherTimer? _autoTimer;
    private DispatcherTimer? _tempTimer;
    private bool _tempBusy;
    private readonly AlertStore _alertStore = new();
    private readonly HistoryStore _history = new();

    private readonly DeviceEnumerator _enumerator = new();
    private readonly SmartAttributeDb? _attrDb = SmartAttributeDb.Load();
    private readonly DrivedbIndex? _drivedb = DrivedbIndex.Load();

    public ObservableCollection<PhysicalDriveInfo> Drives { get; } = [];
    public ObservableCollection<TickVm> Ticks { get; } = [];
    public ObservableCollection<VerdictItem> Findings { get; } = [];
    public ObservableCollection<SmartAttribute> Attributes { get; } = [];
    public ObservableCollection<AlertItem> RecentAlerts { get; } = [];
    private readonly Dictionary<string, DiskAlertState> _alertStates = [];
    /// <summary>新告警（托盘用）。</summary>
    public event Action<AlertItem>? AlertRaised;

    private PhysicalDriveInfo? _selected;
    public PhysicalDriveInfo? SelectedDrive
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDrive));
            _ = RefreshAsync();
        }
    }

    private DriveDiagnostic? _diag;
    public DriveDiagnostic? Diagnostic
    {
        get => _diag;
        private set { _diag = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDiagnostic)); }
    }

    private string _status = "就绪";
    public string StatusText { get => _status; set { _status = value; OnPropertyChanged(); } }

    private bool _busy;
    public bool IsBusy { get => _busy; set { _busy = value; OnPropertyChanged(); OnPropertyChanged(nameof(NotBusy)); } }

    public bool HasDrive => _selected is not null;
    public bool HasDiagnostic => _diag is not null;
    public bool NotBusy => !_busy;

    public string AdminText { get; private set; } = "";
    public bool IsAdmin { get; private set; }

    public ICommand RescanCommand { get; }
    public ICommand RefreshCommand { get; }

    public MainViewModel()
    {
        RescanCommand = new RelayCommand(_ => Scan());
        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsBusy);
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            IsAdmin = new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { }
        AdminText = IsAdmin ? "管理员 · 直通可用" : "受限 · 无直通权限";

        ApplyAutoRefresh();
        ApplyTempRefresh();
        LoadAlertHistory();
        Scan();
    }

    /// <summary>枚举设备并选中第一块。</summary>
    public void Scan()
    {
        try
        {
            var keep = _selected?.DiskKey;
            Drives.Clear();
            var list = _enumerator.Enumerate();
            foreach (var d in list) Drives.Add(d);
            StatusText = "发现 " + Drives.Count + " 块物理磁盘"
                + (_attrDb is null ? "（未加载特征库：" + AppPaths.FindProductData() + "）" : "");
            SelectedDrive = Drives.FirstOrDefault(d => d.DiskKey == keep) ?? Drives.FirstOrDefault();
        }
        catch (Exception ex) { StatusText = "枚举失败：" + ex.Message; }
    }

    /// <summary>后台跑完整诊断（双引擎 SMART + 介质判定 + 状态判定）。</summary>
    public async Task RefreshAsync()
    {
        if (_selected is null) return;
        var drive = _selected;
        IsBusy = true;
        StatusText = "正在读取 " + drive.Model + " …";
        try
        {
            var diag = await Task.Run(() => DriveDiagnostics.Run(drive, crossCheck: true, "auto", _attrDb, _drivedb));
            if (!ReferenceEquals(drive, _selected)) return;   // 期间用户换盘了

            // **身份回填**：USB 桥接盘最初枚举到的是桥接芯片的身份（Realtek RTL9210B-CG），
            // smartctl 穿过桥后读到的是盘自己（KIOXIA EXCERIA PLUS G3）。不同步的话，
            // 顶部标题、历史目录名、DiskKey 全挂在桥上 —— 换个盒子同一块盘就变成"另一块盘"。
            if (diag.Smart.Identity is { } real
                && !string.IsNullOrWhiteSpace(real.Model)
                && !real.Model.Equals(drive.Model, StringComparison.OrdinalIgnoreCase))
            {
                int idx = Drives.IndexOf(drive);
                if (idx >= 0)
                {
                    string oldModel = drive.Model;          // 先记下桥接名，覆盖后就没了
                    var corrected = drive with { Model = real.Model, Serial = real.Serial, Firmware = real.Firmware };
                    Drives[idx] = corrected;
                    _selected = corrected;
                    drive = corrected;
                    StatusText = "已识别真实盘体：" + real.Model + "（此前显示的是桥接芯片 " + oldModel + "）";
                }
            }

            Diagnostic = diag;
            Attributes.Clear();
            foreach (var a in diag.Smart.Attributes) Attributes.Add(a);
            Findings.Clear();
            foreach (var f in diag.Verdict.Findings) Findings.Add(f);

            EvaluateAlerts(diag, drive);

            // 诊断里本来就带温度，顺手同步到"实时温度"，避免界面先空几秒
            if (TemperatureOf(diag) is { } dt)
            {
                TemperatureCelsius = dt;
                TemperatureAt = DateTime.Now;
                OnPropertyChanged(nameof(TemperatureText));
            }

            RecordHistory(diag, drive);

            StatusText = diag.Smart.Success
                ? drive.Model + " · " + diag.Verdict.Label
                  + " · 引擎 " + diag.Smart.Engine
                  + (diag.CrossCheck.Count > 0 ? " · 交叉校验 " + diag.CrossCheck.Count + " 项" : "")
                : drive.Model + " · SMART 读取失败：" + diag.Smart.Error;
        }
        catch (Exception ex) { StatusText = "诊断失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>按"状态变化"评估告警：温度持续偏高只报一次，恢复时再报一条。</summary>
    private void EvaluateAlerts(DriveDiagnostic diag, PhysicalDriveInfo drive)
    {
        try
        {
            double? temp = diag.Smart.Nvme?.CompositeTemperatureC;
            long critical = diag.Smart.Attributes.Count(a => a.Status is "注意" or "危险");
            long mediaErrors = (long)(diag.Smart.Nvme?.MediaErrors ?? 0);

            _alertStates.TryGetValue(drive.DiskKey, out var prev);
            var alerts = AlertEvaluator.Evaluate(prev, drive.DiskKey, drive.Model,
                diag.Verdict.State, temp, diag.Smart.Success, critical, mediaErrors);

            _alertStates[drive.DiskKey] = AlertEvaluator.Snapshot(drive.DiskKey, drive.Model,
                diag.Verdict.State, temp, diag.Smart.Success, critical);

            foreach (var a in alerts)
            {
                RecentAlerts.Insert(0, a);
                _alertStore.Append(a);                 // 落盘：关掉程序也留痕（CDI 的 ALARM_HISTORY）
                Services.SoundAlert.Play(a.Level);     // 提示音（可在设置里关）
                AlertRaised?.Invoke(a);
            }
            while (RecentAlerts.Count > 50) RecentAlerts.RemoveAt(RecentAlerts.Count - 1);
        }
        catch { /* 告警失败不影响诊断 */ }
    }


    /// <summary>从磁盘读回历史告警（启动时用）。</summary>
    public void LoadAlertHistory()
    {
        foreach (var a in _alertStore.Recent(50))
        {
            RecentAlerts.Add(a);
            while (RecentAlerts.Count > 50) RecentAlerts.RemoveAt(RecentAlerts.Count - 1);
        }
    }

    public int AlertHistoryCount => _alertStore.Count();
    public void ClearAlertHistory() { _alertStore.Clear(); RecentAlerts.Clear(); }

    /// <summary>实时温度（℃）。由独立的温度定时器刷新，取值只走原生引擎。</summary>
    public double? TemperatureCelsius { get; private set; }

    /// <summary>温度最近一次更新的时间。</summary>
    public DateTime? TemperatureAt { get; private set; }

    /// <summary>温度刷新文案（界面用）。</summary>
    public string TemperatureText => TemperatureCelsius is { } t
        ? UnitConverter.FormatTemp(t, Settings.Fahrenheit)
        : "-";

    /// <summary>按设置重建"温度"这一路的定时器。</summary>
    public void ApplyTempRefresh()
    {
        _tempTimer?.Stop();
        _tempTimer = null;
        int sec = Math.Clamp(Settings.TempRefreshSeconds, 1, 3600);
        _tempTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(sec) };
        _tempTimer.Tick += async (_, _) => await RefreshTemperatureAsync();
        _tempTimer.Start();
    }

    /// <summary>
    /// <summary>
    /// 只读温度：首选原生引擎（约 20 ms）；原生失败时兜底走 smartctl（约 280 ms）。
    ///
    /// 兜底这条路是为 USB-NVMe 桥接盘加的 —— 盒子里是 NVMe 盘，原生引擎发 ATA 命令必然失败
    /// （现在直接跳过），而悬浮窗的温度不能因此变成"--"。smartctl 慢一些，
    /// 但温度刷新默认 10 秒一次，280 ms 的开销完全可接受。
    /// 失败时保持上一次的值不变，避免界面上的温度忽明忽暗。
    /// </summary>
    public async Task RefreshTemperatureAsync()
    {
        var drive = _selected;
        if (drive is null || _tempBusy) return;
        _tempBusy = true;
        try
        {
            double? t = null;
            var native = await Task.Run(() => SmartProbe.Read(drive, "auto"));
            if (!ReferenceEquals(drive, _selected)) return;   // 期间换盘了
            if (native.Nvme?.CompositeTemperatureC is { } nv) t = nv;

            // 原生拿不到（USB 桥接盘 / 权限不足）→ smartctl 兜底。只在原生失败时跑，不白花 280 ms。
            if (t is null && !native.Success)
            {
                var sc = await Task.Run(() => new SmartctlEngine().Read(drive));
                if (!ReferenceEquals(drive, _selected)) return;
                t = sc.Parsed?.Nvme?.CompositeTemperatureC;
            }

            if (t is { } temp)
            {
                TemperatureCelsius = temp;
                TemperatureAt = DateTime.Now;
                OnPropertyChanged(nameof(TemperatureCelsius));
                OnPropertyChanged(nameof(TemperatureAt));
                OnPropertyChanged(nameof(TemperatureText));
            }
        }
        catch { /* 温度读不到就保持原值 */ }
        finally { _tempBusy = false; }
    }

    /// <summary>
    /// 要进历史的累计型计数（ATA）。
    /// 这四个正是 Google/Backblaze 认定的强预测指标 ——
    /// 单看绝对值只能判断"有没有坏道"，配合历史才能判断"坏道是新的还是老的"。
    /// </summary>
    private static readonly (int Id, string Name)[] CounterMetrics =
    [
        (0x05, "Reallocated"),
        (0xC5, "Pending"),
        (0xC6, "Uncorrectable"),
        (0xC7, "CrcErrors"),
    ];

    /// <summary>取温度：NVMe 走健康日志，ATA 走 0xC2（气流温度 0xBE 兜底）。</summary>
    private static double? TemperatureOf(DriveDiagnostic d)
    {
        if (d.Smart.Nvme?.CompositeTemperatureC is { } nv && nv > 0) return nv;
        foreach (var a in d.Smart.Attributes)
            if ((a.Id == 0xC2 || a.Id == 0xBE) && (a.Raw & 0xFF) is > 0 and < 120)
                return a.Raw & 0xFF;
        return null;
    }

    /// <summary>
    /// 记一个历史点。
    ///
    /// **采样节流**：同值且距上一点不足 1 小时就跳过，值变了（≥0.5 ℃）立刻记。
    /// 不做节流的话，自动刷新一开一天能写几千行，曲线还没法看（一片毛刺）。
    /// 这是参照软件的做法 —— 有变化才落点。
    /// </summary>
    private void RecordHistory(DriveDiagnostic diag, PhysicalDriveInfo drive)
    {
        try
        {
            if (TemperatureOf(diag) is not { } temp) return;
            var last = _history.Query(drive.Model, drive.Serial, "Temperature");
            if (last.Count > 0)
            {
                var (when, val) = last[^1];
                bool changed = Math.Abs(temp - val) >= 0.5;
                if (!changed && (DateTime.Now - when).TotalHours < 1) return;
            }
            _history.Append(drive.Model, drive.Serial, "Temperature", DateTime.Now, Math.Round(temp, 1));

            // 累计型计数也要记 —— 没有历史就没法判断"这个坏道是新的还是躺了三年的"。
            // 它们单调不减，所以**只在变化时记**（同值不重复写，历史文件不会膨胀）。
            foreach (var map in CounterMetrics)
            {
                var a = diag.Smart.Ata?.Attributes.FirstOrDefault(x => x.Id == map.Id);
                if (a is null) continue;
                var prev = _history.Query(drive.Model, drive.Serial, map.Name);
                if (prev.Count > 0 && (ulong)prev[^1].Value == a.Raw) continue;
                _history.Append(drive.Model, drive.Serial, map.Name, DateTime.Now, a.Raw);
            }
        }
        catch { /* 记历史失败不影响诊断 */ }
    }

    /// <summary>按设置重建自动刷新定时器（0 = 关闭）。</summary>
    public void ApplyAutoRefresh()
    {
        _autoTimer?.Stop();
        _autoTimer = null;
        int sec = Settings.EffectiveRefreshSeconds;
        if (sec <= 0) return;
        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(sec) };
        _autoTimer.Tick += async (_, _) => await RefreshAsync();
        _autoTimer.Start();
    }

    /// <summary>设置变化后保存并应用。</summary>
    public void SaveSettings()
    {
        Settings.Save();
        ApplyAutoRefresh();
        ApplyTempRefresh();
        OnPropertyChanged(nameof(Settings));
    }


    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
