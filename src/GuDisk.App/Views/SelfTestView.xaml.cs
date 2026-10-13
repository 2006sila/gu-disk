using System.Windows;
using System.Windows.Controls;
using GuDisk.Core.App;
using GuDisk.Core.Devices;
using GuDisk.Core.Smart;

namespace GuDisk.App.Views;

public partial class SelfTestView : UserControl
{
    private PhysicalDriveInfo? _drive;
    private AamApm.Values? _values;

    public SelfTestView() => InitializeComponent();

    public void Show(PhysicalDriveInfo drive)
    {
        _drive = drive;
        ElevateBtn.Visibility = AdminHelper.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
        EngineText.Text = AdminHelper.IsAdmin ? "管理员 · 自检可用" : "受限 · 自检需要管理员";
        Reload();
    }

    private void Reload()
    {
        if (_drive is null) return;

        var (okSelf, errSelf, entries, _) = SelfTest.ReadSelfTestLog(_drive);
        if (okSelf)
        {
            SelfLogList.ItemsSource = entries;
            SelfLogHeader.Text = "自检日志（" + entries.Count + " 条）";
            StatusText.Text = entries.Count == 0 ? "尚无自检记录。点「短自检」开始一次。" : "";
        }
        else
        {
            SelfLogList.ItemsSource = null;
            SelfLogHeader.Text = "自检日志";
            StatusText.Text = "自检日志不可读：" + errSelf;
        }

        var (okErr, errErr, errs) = SelfTest.ReadErrorLog(_drive);
        ErrLogList.ItemsSource = okErr ? errs : null;
        ErrLogHeader.Text = okErr ? "错误日志（" + errs.Count + " 条）"
                                  : "错误日志（不可读：" + errErr + "）";

        var (okAam, errAam, v) = AamApm.Read(_drive);
        if (!okAam)
        {
            _values = null;
            AamValue.Text = ApmValue.Text = "--";
            AamStatus.Text = errAam ?? "不可读";
            return;
        }
        _values = v;
        AamValue.Text = v!.Aam is { } a ? "0x" + a.ToString("X2") + "（" + a + "）" : "不支持";
        ApmValue.Text = v.Apm is { } p ? "0x" + p.ToString("X2") + "（" + p + "）" : "不支持";
        AamStatus.Text = (v.AamSupported || v.ApmSupported)
            ? "厂商推荐值：AAM " + (v.AamRecommended?.ToString() ?? "-") + " · APM " + (v.ApmRecommended?.ToString() ?? "-")
            : "该盘不支持 AAM/APM（SSD 通常不支持）。";
    }

    private async void OnShort(object sender, RoutedEventArgs e) => await Run(SelfTestKind.Short);
    private async void OnExtended(object sender, RoutedEventArgs e) => await Run(SelfTestKind.Extended);
    private async void OnAbort(object sender, RoutedEventArgs e) => await Run(SelfTestKind.Abort);

    private async Task Run(SelfTestKind kind)
    {
        if (_drive is null) return;
        SetBusy(true);
        try
        {
            var r = await Task.Run(() => SelfTest.Start(_drive, kind));
            StatusText.Text = r.Success ? r.Message + "　（通道 " + r.Transport + "）" : "失败：" + r.Error;
            if (r.Success) await Task.Delay(800);
            Reload();
        }
        catch (Exception ex) { StatusText.Text = "异常：" + ex.Message; }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        ShortBtn.IsEnabled = ExtBtn.IsEnabled = AbortBtn.IsEnabled = RefreshBtn.IsEnabled = !busy;
        if (busy) StatusText.Text = "正在下发命令…";
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (AdminHelper.RestartElevated())
            Application.Current.Shutdown();
        else
            StatusText.Text = "提权被取消（或 UAC 被拒绝）。可以手动右键程序 → 以管理员身份运行。";
    }

    private async void ApplyAam(byte value)
    {
        if (_drive is null) return;
        var (ok, err, we) = await Task.Run(() => AamApm.SetAam(_drive, value));
        AamStatus.Text = ok ? "AAM 已设为 0x" + value.ToString("X2") : err + "（Win32 " + we + "）";
        Reload();
    }

    private async void ApplyApm(byte value)
    {
        if (_drive is null) return;
        var (ok, err, we) = await Task.Run(() => AamApm.SetApm(_drive, value));
        AamStatus.Text = ok ? "APM 已设为 0x" + value.ToString("X2") : err + "（Win32 " + we + "）";
        Reload();
    }

    private void OnAamQuiet(object s, RoutedEventArgs e) => ApplyAam(0x80);
    private void OnAamRecommended(object s, RoutedEventArgs e) => ApplyAam((byte)(_values?.AamRecommended ?? 0x80));
    private void OnAamFast(object s, RoutedEventArgs e) => ApplyAam(0xFE);
    private void OnAamOff(object s, RoutedEventArgs e) => ApplyAam(0x00);
    private void OnApmLow(object s, RoutedEventArgs e) => ApplyApm(0x01);
    private void OnApmMid(object s, RoutedEventArgs e) => ApplyApm(0x80);
    private void OnApmHigh(object s, RoutedEventArgs e) => ApplyApm(0xFE);
    private void OnApmOff(object s, RoutedEventArgs e) => ApplyApm(0x00);
}
