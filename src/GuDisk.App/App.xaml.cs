using System.IO;
using System.Windows;
using GuDisk.Core.App;

namespace GuDisk.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 界面线程异常必须落地成文件，否则只剩一个"应用程序错误"对话框
        DispatcherUnhandledException += (_, args) =>
        {
            Crash(args.Exception, "UI 线程");
            args.Handled = true;
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Crash(args.ExceptionObject as Exception, "非 UI 线程");

        base.OnStartup(e);
        try
        {
            AppPaths.EnsureCreated();
            Log("启动 " + GuDisk.Core.GuDiskInfo.ProductName + " " + GuDisk.Core.GuDiskInfo.Version
                + " 数据根=" + AppPaths.Root);
        }
        catch (Exception ex)
        {
            Log("启动初始化失败: " + ex.Message);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("退出");
        base.OnExit(e);
    }

    private static void Crash(Exception? ex, string where)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            var file = Path.Combine(AppPaths.Logs, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file,
                $"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}"
                + $"位置: {where}{Environment.NewLine}"
                + $"版本: {GuDisk.Core.GuDiskInfo.Version}{Environment.NewLine}{Environment.NewLine}"
                + ex?.ToString());
        }
        catch { }
    }

    private static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            var file = Path.Combine(AppPaths.Logs, $"gu-disk-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}
