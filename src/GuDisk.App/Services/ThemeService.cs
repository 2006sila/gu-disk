using System.Windows;

namespace GuDisk.App.Services;

/// <summary>
/// 深浅主题切换。**唯一入口** —— 顶栏的 ◐ 和设置页的按钮都调它。
///
/// 早先顶栏自己写了一份替换逻辑、设置页只抛了个没人订阅的事件，
/// 结果设置页按钮"点了没反应"。这类交互必须有单一实现 + 显式订阅。
/// </summary>
public sealed class ThemeService
{
    public static ThemeService Instance { get; } = new();

    public bool IsLight { get; private set; }

    /// <summary>主题已切换（供需要重设命令式画刷的地方刷新）。</summary>
    public event Action? Changed;

    public void Toggle() => Set(!IsLight);

    public void Set(bool light)
    {
        IsLight = light;
        string file = light ? "Themes/Tokens.Light.xaml" : "Themes/Tokens.Dark.xaml";

        // 按 Source 定位令牌字典，**不要按索引** —— 索引重排会误替换样式字典，
        // 症状是"底色变了但所有自绘模板消失"。
        var dicts = Application.Current.Resources.MergedDictionaries;
        int idx = -1;
        for (int i = 0; i < dicts.Count; i++)
        {
            string src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("Tokens.", StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        }
        var next = new ResourceDictionary { Source = new Uri(file, UriKind.Relative) };
        if (idx >= 0) dicts[idx] = next;
        else dicts.Insert(0, next);

        Changed?.Invoke();
    }

    public string Label => IsLight ? "当前：浅色" : "当前：深色";
}
