using System.Windows;

namespace GuDisk.App.Services;

/// <summary>
/// 应用图标的运行时入口（窗口与托盘共用）。
///
/// 为什么要读嵌入资源而不是磁盘文件：exe 的图标（ApplicationIcon）只管资源管理器显示，
/// 运行时拿不到；托盘以前写死 System.Drawing.SystemIcons.Application —— 那是 .NET 的通用图标，
/// 跟 Gu-disk 没有任何关系。这里统一从 Assets\gudisk.ico 读。
/// </summary>
public static class AppIcon
{
    private static System.Drawing.Icon? _cached;

    /// <summary>托盘 / 悬浮窗要的 System.Drawing.Icon（按 16px 档取，托盘就是这个尺寸）。</summary>
    public static System.Drawing.Icon Tray()
    {
        if (_cached is not null) return _cached;
        try
        {
            var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/gudisk.ico"))?.Stream;
            _cached = s is null ? System.Drawing.SystemIcons.Application
                     : new System.Drawing.Icon(s, new System.Drawing.Size(16, 16));
        }
        catch { _cached = System.Drawing.SystemIcons.Application; }
        return _cached;
    }
}
