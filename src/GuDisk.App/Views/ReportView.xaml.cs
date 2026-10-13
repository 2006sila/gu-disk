using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GuDisk.Core.App;
using GuDisk.Core.Benchmark;
using GuDisk.Core.Devices;
using GuDisk.Core.History;
using GuDisk.Core.Report;
using GuDisk.Core.Scan;

namespace GuDisk.App.Views;

public partial class ReportView : UserControl
{
    private PhysicalDriveInfo? _drive;
    private DriveDiagnostic? _diag;
    private string? _lastPath;

    public ReportView() => InitializeComponent();

    public void Show(PhysicalDriveInfo drive, DriveDiagnostic? diag, string status)
    {
        _drive = drive;
        _diag = diag;
        StatusText.Text = status;
        if (diag is not null && Preview.Text.Length == 0)
            Preview.Text = ReportWriter.ToText(new ReportInput(diag));
    }

    private async void OnGenerate(object sender, RoutedEventArgs e)
    {
        if (_drive is null || _diag is null) return;
        string format = FormatBox.SelectedIndex switch { 1 => "txt", 2 => "json", _ => "html" };
        string ext = format == "txt" ? "txt" : format;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = ext.ToUpperInvariant() + "|*." + ext,
            FileName = "Gu-disk-report-" + _drive.DeviceNumber + "." + ext
        };
        if (dlg.ShowDialog() != true) return;

        GenBtn.IsEnabled = false;
        try
        {
            BenchResult? bench = null;
            ScanResult? scan = null;
            string dir = Path.GetDirectoryName(dlg.FileName)!;

            if (BenchBox.IsChecked == true)
            {
                StatusText.Text = "正在测速（256M）…";
                bench = await Task.Run(() => BenchRunner.Run(
                    Path.Combine(dir, "Gu-disk_bench.tmp"), 256L * 1024 * 1024,
                    BenchProfiles.Cdm4, "random", 1, false, false));
            }
            if (ScanBox.IsChecked == true)
            {
                StatusText.Text = "正在扫描（1G）…";
                scan = await Task.Run(() => SurfaceScanner.RunFile(
                    Path.Combine(dir, "Gu-disk_scan.tmp"), 1024L * 1024 * 1024));
            }

            StatusText.Text = "正在生成报告…";
            IReadOnlyList<(DateTime, double)> temp = [];
            try { temp = new HistoryStore().Query(_drive.Model, _drive.Serial, "Temperature").Select(x => (x.When, x.Value)).ToList(); }
            catch { }

            var input = new ReportInput(_diag, bench, null, scan, temp.Count > 0 ? temp : null);
            string content = format switch
            {
                "txt" => ReportWriter.ToText(input),
                "json" => ReportWriter.ToJson(input),
                _ => ReportWriter.ToHtml(input)
            };
            File.WriteAllText(dlg.FileName, content, new System.Text.UTF8Encoding(false));
            _lastPath = dlg.FileName;
            OpenBtn.IsEnabled = CopyBtn.IsEnabled = true;
            Preview.Text = ReportWriter.ToText(input);
            StatusText.Text = "已写入 " + dlg.FileName + "   " + (content.Length / 1024) + " KB"
                + (format == "html" ? "（单文件，可直接转发）" : "");
        }
        catch (Exception ex) { StatusText.Text = "生成失败：" + ex.Message; }
        finally { GenBtn.IsEnabled = true; }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_lastPath is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + _lastPath + "\"") { UseShellExecute = true });
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Preview.Text.Length > 0) Clipboard.SetText(Preview.Text);
        StatusText.Text = "已复制到剪贴板";
    }
}
