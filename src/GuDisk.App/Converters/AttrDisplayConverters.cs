using System.Globalization;
using System.Windows.Data;
using GuDisk.Core.App;
using GuDisk.Core.Decode;
using GuDisk.Core.Smart;

namespace GuDisk.App.Converters;

/// <summary>按偏好显示属性名（中文 / 英文）。</summary>
public sealed class AttrNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SmartAttribute a) return "";
        return AppSettings.Current.EnglishNames && !string.IsNullOrEmpty(a.NameEn) ? a.NameEn : a.NameCn;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>
/// 原始值文本：温度类属性按偏好换算摄氏/华氏，其余原样。
/// 温度属性只有两种编码是明确的：NVMe 0x02 的原始值是开尔文；ATA 0xC2/0xBE 的低字节是当前摄氏。
/// </summary>
public sealed class AttrValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SmartAttribute a) return "";
        if (!AppSettings.Current.Fahrenheit) return a.RawMeaning;

        double? celsius = a.Id switch
        {
            0x02 when a.Category == "环境" && a.Raw > 200 => a.Raw - 273.15,   // NVMe 开尔文
            0x12 or 0x13 or 0x14 or 0x15 => a.Raw > 200 ? a.Raw - 273.15 : null,
            0xC2 or 0xBE => a.Raw & 0xFF,
            _ => null,
        };
        if (celsius is null) return a.RawMeaning;
        return UnitConverter.FormatTemp(celsius.Value, fahrenheit: true)
            + "（原始 " + celsius.Value.ToString("0.#") + " ℃）";
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
