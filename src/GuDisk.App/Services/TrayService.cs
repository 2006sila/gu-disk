using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using GuDisk.Core.App;

namespace GuDisk.App.Services;

/// <summary>
/// 托盘常驻。用 WinForms 的 NotifyIcon（WindowsDesktop 自带，不需要第三方包）。
/// 关闭窗口不退出程序，只在托盘留一个图标；双击显示主窗口。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Window _window;
    private AlertLevel _level = AlertLevel.Info;

    public TrayService(Window window)
    {
        _window = window;
        _icon = new NotifyIcon
        {
            Icon = AppIcon.Tray(),
            Visible = true,
            Text = "Gu-disk · 磁盘全检",
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => Restore());
        menu.Items.Add("温度悬浮窗", null, (_, _) => OnToggleOverlay?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => System.Windows.Application.Current.Shutdown());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => Restore();
    }

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>托盘菜单「温度悬浮窗」的开关回调（由宿主窗口提供）。</summary>
    public Action? OnToggleOverlay { get; set; }

    private void Restore()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Minimize()
    {
        if (!MinimizeToTray) return;
        _window.Hide();
        _icon.ShowBalloonTip(2000, "Gu-disk 仍在运行", "双击托盘图标可以重新打开窗口。", ToolTipIcon.Info);
    }

    /// <summary>更新托盘提示（受 63 字符限制，必须截断）。</summary>
    public void UpdateTooltip(string text)
    {
        string t = text.Length > 62 ? text[..61] + "…" : text;
        _icon.Text = t;
    }

    public void Notify(AlertItem a)
    {
        var kind = a.Level switch
        {
            AlertLevel.Critical => ToolTipIcon.Error,
            AlertLevel.Warning => ToolTipIcon.Warning,
            _ => ToolTipIcon.Info
        };
        if (a.Level > _level) _level = a.Level;
        _icon.ShowBalloonTip(6000, "Gu-disk · " + a.Title, a.Detail, kind);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        // 不 Dispose 图标本身：AppIcon.Tray() 返回的是**进程内共享的缓存实例**，悬浮窗还要用同一份。
        // NotifyIcon 也不接管 Icon 的所有权。
        // （原来这里有 _ownIcon?.Dispose()，而 _ownIcon 从未被赋值 —— 编译器 CS0649 点出来的死代码。）
    }
}
