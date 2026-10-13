using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GuDisk.Core.App;
using GuDisk.Core.Smart;

namespace GuDisk.App.Views;

public partial class SmartView : UserControl
{
    public SmartView() => InitializeComponent();

    public void Show(IReadOnlyList<SmartAttribute> attributes, bool isNvme, string engine)
    {
        AttrList.ItemsSource = attributes;
        CountText.Text = attributes.Count + " 项 · " + (isNvme ? "NVMe Health Log" : "ATA 属性表")
            + " · 引擎 " + engine;
        if (attributes.Count > 0) AttrList.SelectedIndex = 0;
    }

    /// <summary>复制整张属性表为 TSV（可直接粘进 Excel）。</summary>
    private void OnCopyTable(object sender, RoutedEventArgs e)
    {
        if (AttrList.ItemsSource is not IEnumerable<SmartAttribute> rows) return;
        var sb = new StringBuilder();
        sb.AppendLine("ID\t属性名\t当前\t最差\t阈值\t原始值(十进制)\t原始值(十六进制)\t状态\t分类");
        bool en = AppSettings.Current.EnglishNames;
        foreach (var a in rows)
            sb.AppendLine(string.Join('\t',
                "0x" + a.Id.ToString("X2"),
                en && !string.IsNullOrEmpty(a.NameEn) ? a.NameEn : a.NameCn,
                a.Current, a.Worst, a.Threshold, a.Raw, a.RawHex, a.Status, a.Category));
        Clipboard.SetText(sb.ToString());
        CountText.Text = "已复制 " + (sb.ToString().Split('\n').Length - 2) + " 行到剪贴板";
    }

    /// <summary>把属性表渲染成 PNG（CDI 的 SAVE_IMAGE 对应功能）。</summary>
    private void OnSaveImage(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PNG|*.png", FileName = "Gu-disk-smart.png" };
        if (dlg.ShowDialog() != true) return;

        // 离屏重建一次表格，避免只截到可视区域
        var dpi = 2.0;
        var target = (FrameworkElement)sender;
        var element = (FrameworkElement)AttrList;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            dc.DrawRectangle(brush, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)(element.ActualWidth * dpi)), Math.Max(1, (int)(element.ActualHeight * dpi)),
            96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(dlg.FileName);
        enc.Save(fs);
        CountText.Text = "已保存 " + dlg.FileName;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AttrList.SelectedItem is not SmartAttribute a) return;
        DetailHint.Visibility = System.Windows.Visibility.Collapsed;
        DetailBody.Visibility = System.Windows.Visibility.Visible;
        DetailName.Text = AppSettings.Current.EnglishNames && !string.IsNullOrEmpty(a.NameEn) ? a.NameEn : a.NameCn;
        DetailId.Text = "ID 0x" + a.Id.ToString("X2") + "   ·   分类 " + a.Category;
        DetailRaw.Text = a.RawMeaning;
        DetailStatus.Text = "状态：" + a.Status
            + (a.Threshold > 0 ? "   当前 " + a.Current + " / 最差 " + a.Worst + " / 阈值 " + a.Threshold : "");
        // 两行是同一个数的十进制与十六进制，必须写清楚，否则会被当成两个不同的值
        DetailValues.Text = "十进制    " + a.Raw.ToString("N0")
            + Environment.NewLine + "十六进制  0x" + a.RawHex.TrimStart('0').PadLeft(1, '0')
            + Environment.NewLine + "补零十六进制  " + a.RawHex;
    }
}
