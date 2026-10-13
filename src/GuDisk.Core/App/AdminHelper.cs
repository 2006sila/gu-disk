using System.Diagnostics;

namespace GuDisk.Core.App;

/// <summary>
/// 提权与系统管理工具入口。
/// 磁盘 SMART / 自检 / 坏道扫描都要管理员权限，而"只显示受限"对用户毫无帮助 ——
/// 这里提供一键以管理员身份重启（UAC 由 ShellExecute 的 runas 触发）。
/// </summary>
public static class AdminHelper
{
    public static bool IsAdmin { get; } = Check();

    private static bool Check()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>以管理员身份重新启动自己。用户拒绝 UAC 时返回 false（不抛）。</summary>
    public static bool RestartElevated(string? arguments = null)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            if (!string.IsNullOrEmpty(arguments)) psi.Arguments = arguments;
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    public static bool OpenDiskManagement() => Open("diskmgmt.msc");
    public static bool OpenDeviceManager() => Open("devmgmt.msc");
    public static bool OpenFolder(string path) => Open(path);

    private static bool Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); return true; }
        catch { return false; }
    }
}
