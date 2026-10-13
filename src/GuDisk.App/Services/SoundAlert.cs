using System.Runtime.InteropServices;

namespace GuDisk.App.Services;

/// <summary>
/// 告警提示音。用 MessageBeep 而不是 System.Media —— 后者在 .NET Core 下要额外的兼容包。
/// 用经典 DllImport（LibraryImport 是源生成器，要求 /unsafe）。
/// </summary>
internal static class SoundAlert
{
    private const uint MB_ICONHAND = 0x10, MB_ICONEXCLAMATION = 0x30, MB_ICONASTERISK = 0x40;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint uType);

    public static void Play(Core.App.AlertLevel level)
    {
        if (!Core.App.AppSettings.Current.AlertSound) return;
        try
        {
            _ = MessageBeep(level switch
            {
                Core.App.AlertLevel.Critical => MB_ICONHAND,
                Core.App.AlertLevel.Warning => MB_ICONEXCLAMATION,
                _ => MB_ICONASTERISK,
            });
        }
        catch { }
    }
}
