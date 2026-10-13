using GuDisk.Core.Decode;

namespace GuDisk.Core.Smart;

/// <summary>
/// NVMe SMART / Health Information Log（512 B）解码。
/// 字段偏移按 NVMe 规范 Figure 208；属性名沿用 CrystalDiskInfo 的 [SmartNVMe] 节文案，
/// M2 会改为从 data/smart-attributes.json 读取（由 CDI 语言包脚本生成）。
/// </summary>
public static class NvmeSmartReader
{
    public const int LogSize = 512;

    /// <summary>属性 ID → 中文名（取自 CDI Simplified Chinese.lang 的 [SmartNVMe] 节）。</summary>
    private static readonly Dictionary<byte, string> Names = new()
    {
        [0x01] = "严重警告标志",
        [0x02] = "综合温度",
        [0x03] = "可用备用空间",
        [0x04] = "可用备用空间阈值",
        [0x05] = "已用寿命百分比",
        [0x06] = "读取单位计数",
        [0x07] = "写入单位计数",
        [0x08] = "主机读命令计数",
        [0x09] = "主机写命令计数",
        [0x0A] = "控制器忙状态时间",
        [0x0B] = "启动-关闭循环次数",
        [0x0C] = "通电时间（小时）",
        [0x0D] = "不安全关机计数",
        [0x0E] = "介质与数据完整性错误计数",
        [0x0F] = "错误日志项数",
        [0x10] = "警告综合温度时间",
        [0x11] = "严重综合温度时间",
        [0x12] = "温度传感器 1",
        [0x13] = "温度传感器 2",
        [0x14] = "温度传感器 3",
        [0x15] = "温度传感器 4",
        [0x16] = "温度传感器 5",
        [0x17] = "温度传感器 6",
        [0x18] = "温度传感器 7",
        [0x19] = "温度传感器 8",
        [0x1A] = "热管理温度 1 转换次数",
        [0x1B] = "热管理温度 2 转换次数",
        [0x1C] = "热管理温度 1 总时间",
        [0x1D] = "热管理温度 2 总时间",
    };

    public static string NameOf(byte id) => Names.TryGetValue(id, out var n) ? n : "厂商定义 (0x" + id.ToString("X2") + ")";

    /// <summary>解析 512 字节健康日志；长度不足返回 null。</summary>
    public static NvmeSmartLog? Parse(byte[] data)
    {
        if (data.Length < LogSize) return null;

        var sensors = new List<short>();
        for (int i = 0; i < 8; i++)
        {
            short k = BitConverter.ToInt16(data, 200 + i * 2);
            if (k > 0) sensors.Add(k);
        }

        return new NvmeSmartLog(
            CriticalWarning: data[0],
            CompositeTemperatureK: BitConverter.ToInt16(data, 1),
            AvailableSpare: data[3],
            AvailableSpareThreshold: data[4],
            PercentageUsed: data[5],
            DataUnitsRead: (ulong)BitConverter.ToUInt64(data, 32),
            DataUnitsWritten: (ulong)BitConverter.ToUInt64(data, 48),
            HostReadCommands: (ulong)BitConverter.ToUInt64(data, 64),
            HostWriteCommands: (ulong)BitConverter.ToUInt64(data, 80),
            ControllerBusyTime: (ulong)BitConverter.ToUInt64(data, 96),
            PowerCycles: (ulong)BitConverter.ToUInt64(data, 112),
            PowerOnHours: (ulong)BitConverter.ToUInt64(data, 128),
            UnsafeShutdowns: (ulong)BitConverter.ToUInt64(data, 144),
            MediaErrors: (ulong)BitConverter.ToUInt64(data, 160),
            ErrorLogEntries: (ulong)BitConverter.ToUInt64(data, 176),
            WarningTempTimeMinutes: BitConverter.ToUInt32(data, 192),
            CriticalTempTimeMinutes: BitConverter.ToUInt32(data, 196),
            TemperatureSensors: sensors,
            ThermalTransition1: BitConverter.ToUInt32(data, 216),
            ThermalTransition2: BitConverter.ToUInt32(data, 220),
            ThermalTime1Minutes: BitConverter.ToUInt32(data, 224),
            ThermalTime2Minutes: BitConverter.ToUInt32(data, 228),
            Raw512: data);
    }

    /// <summary>转成与 ATA 属性表同构的行，便于 UI 用同一张表渲染。</summary>
    public static IReadOnlyList<SmartAttribute> ToAttributes(NvmeSmartLog nvme)
    {
        var list = new List<SmartAttribute>();
        void Add(byte id, string raw, string hex, string status, string category, ulong numeric = 0)
            => list.Add(new SmartAttribute(id, NameOf(id), "", 0, 0, 0, numeric, hex, raw, status, category));

        Add(0x01, nvme.CriticalWarning == 0 ? "0（正常）" : string.Join(" / ", nvme.CriticalWarningFlags()),
            nvme.CriticalWarning.ToString("X2"), nvme.CriticalWarning == 0 ? "正常" : "危险", "可靠性", nvme.CriticalWarning);
        // 口径统一（曾踩坑）：**两列指向同一个字段整数值**，换算结果只写进文本。
        // 旧版温度行的十六进制用的是"磁盘原始字节序"（39 ℃ 显示 3901 = 0x0139 低字节在前），
        // 而其它行用的是"数值的十六进制" —— 同一列两种口径，看起来就像两列对不上。
        string tempText = nvme.CompositeTemperatureC is { } tc
            ? tc.ToString("0.#") + " ℃（" + nvme.CompositeTemperatureK + " K）"
            : "不可用";
        Add(0x02, tempText, nvme.CompositeTemperatureK.ToString("X4"), "正常", "环境",
            (ulong)nvme.CompositeTemperatureK);
        Add(0x03, nvme.AvailableSpare + " %", nvme.AvailableSpare.ToString("X2"),
            nvme.AvailableSpare >= nvme.AvailableSpareThreshold ? "正常" : "危险", "寿命", nvme.AvailableSpare);
        Add(0x04, nvme.AvailableSpareThreshold + " %", nvme.AvailableSpareThreshold.ToString("X2"), "正常", "寿命", nvme.AvailableSpareThreshold);
        Add(0x05, nvme.PercentageUsed + " %", nvme.PercentageUsed.ToString("X2"),
            nvme.PercentageUsed >= 100 ? "危险" : nvme.PercentageUsed >= 80 ? "注意" : "正常", "寿命", nvme.PercentageUsed);

        void AddCount(byte id, ulong v, string unit, string category)
            => Add(id, UnitConverter.FormatCount((long)v) + " " + unit,
                   v.ToString("X16"), "统计", category, v);

        AddCount(0x06, nvme.DataUnitsRead, "单位（" + UnitConverter.FormatBytes(UnitConverter.NvmeDataUnitsToBytes(nvme.DataUnitsRead)) + "）", "统计");
        AddCount(0x07, nvme.DataUnitsWritten, "单位（" + UnitConverter.FormatBytes(UnitConverter.NvmeDataUnitsToBytes(nvme.DataUnitsWritten)) + "）", "统计");
        AddCount(0x08, nvme.HostReadCommands, "次", "统计");
        AddCount(0x09, nvme.HostWriteCommands, "次", "统计");
        AddCount(0x0A, nvme.ControllerBusyTime, "分钟", "统计");
        AddCount(0x0B, nvme.PowerCycles, "次", "统计");
        Add(0x0C, UnitConverter.FormatCount((long)nvme.PowerOnHours) + " 小时", nvme.PowerOnHours.ToString("X16"), "统计", "统计", nvme.PowerOnHours);
        Add(0x0D, UnitConverter.FormatCount((long)nvme.UnsafeShutdowns) + " 次", nvme.UnsafeShutdowns.ToString("X16"),
            nvme.UnsafeShutdowns > 10 ? "注意" : "正常", "环境", nvme.UnsafeShutdowns);
        Add(0x0E, UnitConverter.FormatCount((long)nvme.MediaErrors), nvme.MediaErrors.ToString("X16"),
            nvme.MediaErrors == 0 ? "正常" : "危险", "可靠性", nvme.MediaErrors);
        Add(0x0F, UnitConverter.FormatCount((long)nvme.ErrorLogEntries), nvme.ErrorLogEntries.ToString("X16"),
            nvme.ErrorLogEntries == 0 ? "正常" : "注意", "可靠性", nvme.ErrorLogEntries);
        Add(0x10, UnitConverter.FormatCount(nvme.WarningTempTimeMinutes) + " 分钟", nvme.WarningTempTimeMinutes.ToString("X8"),
            nvme.WarningTempTimeMinutes == 0 ? "正常" : "注意", "环境", nvme.WarningTempTimeMinutes);
        Add(0x11, UnitConverter.FormatCount(nvme.CriticalTempTimeMinutes) + " 分钟", nvme.CriticalTempTimeMinutes.ToString("X8"),
            nvme.CriticalTempTimeMinutes == 0 ? "正常" : "危险", "环境", nvme.CriticalTempTimeMinutes);

        byte sid = 0x12;
        foreach (var s in nvme.TemperatureSensors)
            Add(sid++, s + " K（" + (s - 273.15).ToString("0.#") + " ℃）", s.ToString("X4"),
                "正常", "环境", (ulong)s);

        Add(0x1A, UnitConverter.FormatCount(nvme.ThermalTransition1), nvme.ThermalTransition1.ToString("X8"), "统计", "环境", nvme.ThermalTransition1);
        Add(0x1B, UnitConverter.FormatCount(nvme.ThermalTransition2), nvme.ThermalTransition2.ToString("X8"), "统计", "环境", nvme.ThermalTransition2);
        Add(0x1C, UnitConverter.FormatCount(nvme.ThermalTime1Minutes) + " 分钟", nvme.ThermalTime1Minutes.ToString("X8"), "统计", "环境", nvme.ThermalTime1Minutes);
        Add(0x1D, UnitConverter.FormatCount(nvme.ThermalTime2Minutes) + " 分钟", nvme.ThermalTime2Minutes.ToString("X8"), "统计", "环境", nvme.ThermalTime2Minutes);

        return list;
    }
}
