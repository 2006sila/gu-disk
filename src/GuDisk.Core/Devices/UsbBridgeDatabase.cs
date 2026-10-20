namespace GuDisk.Core.Devices;

/// <summary>桥接主控判定结果。</summary>
public sealed record BridgeIdentity(UsbBridgeKind Kind, string DisplayName, string Evidence, string ProtocolHint);

/// <summary>
/// USB 桥接主控特征库：VID/PID + 自报字符串 → 桥接类别 + 建议优先尝试的协议。
/// VID/PID 取自常见桥接芯片；自报字符串（BusReportedDeviceDesc）命中时优先级更高。
/// </summary>
public static class UsbBridgeDatabase
{
    private sealed record Entry(UsbBridgeKind Kind, string Name, string Hint,
        ushort? Vid = null, ushort? Pid = null, string? Match = null);

    // 说明：同一颗桥接既做 SATA 盒也做 NVMe 盒的情况很常见，因此 PID 比 VID 更可信。
    private static readonly Entry[] Table =
    [
        // ---- JMicron ----
        new(UsbBridgeKind.JmicronsNvme, "JMicron JMS583 (USB-NVMe)", "sntjmicron", 0x152D, 0x0583),
        new(UsbBridgeKind.JmicronsNvme, "JMicron JMS586/JMS586R (USB-NVMe)", "sntjmicron", 0x152D, 0x0586),
        // **JMicron USB-SATA 一律优先 sat**（2026-10-20 实测：WD20EURS + JMS578 双盘位盒）：
        // "-d usbjmicron" 裸用报 "No device connected"（它要 ,p/,x/,1 之类附加参数），
        // 而 "-d sat" 直接读到真实序列号（WD-WCAZAJ901513 / WD-WCAZAJ217476）。
        new(UsbBridgeKind.JmicronSata, "JMicron JMS578 (USB-SATA)", "sat", 0x152D, 0x0578),
        new(UsbBridgeKind.JmicronSata, "JMicron JMS578/JMS579 双盘位 (USB-SATA)", "sat", 0x152D, 0xA901),
        new(UsbBridgeKind.JmicronSata, "JMicron JMS567/JMS561 (USB-SATA)", "sat", 0x152D, 0x0567),
        new(UsbBridgeKind.Jmb39x, "JMicron JMB39x 多盘位 (USB-SATA)", "jmb39x", 0x152D, 0x0562),
        new(UsbBridgeKind.Jmb39x, "JMicron JMB39x 多盘位 (USB-SATA)", "jmb39x", 0x152D, 0x0539),
        new(UsbBridgeKind.JmicronSata, "JMicron 桥接（PID 未收录）", "sat", 0x152D, null),
        // ---- ASMedia ----
        new(UsbBridgeKind.AsmediaNvme, "ASMedia ASM2362 (USB-NVMe)", "sntasmedia", 0x174C, 0x2362),
        new(UsbBridgeKind.AsmediaNvme, "ASMedia ASM2364 (USB-NVMe 20Gbps)", "sntasmedia", 0x174C, 0x2364),
        new(UsbBridgeKind.AsmediaDual, "ASMedia ASM1352R 双盘位 (USB-SATA)", "usbasmedia", 0x174C, 0x1352),
        new(UsbBridgeKind.AsmediaSata, "ASMedia ASM1351 (USB-SATA)", "usbasmedia", 0x174C, 0x1351),
        new(UsbBridgeKind.AsmediaSata, "ASMedia ASM1153E (USB-SATA)", "usbasmedia", 0x174C, 0x1153),
        new(UsbBridgeKind.AsmediaSata, "ASMedia 桥接（PID 未收录）", "usbasmedia", 0x174C, null),
        // ---- Realtek ----
        new(UsbBridgeKind.RealtekNvme, "Realtek RTL9210/RTL9210B (USB-NVMe)", "sntrealtek", 0x0BDA, 0x9210),
        new(UsbBridgeKind.RealtekNvme, "Realtek RTL9201 (USB-SATA)", "sntrealtek", 0x0BDA, 0x9201),
        new(UsbBridgeKind.RealtekNvme, "Realtek RTL9220 (USB-NVMe 20Gbps)", "sntrealtek", 0x0BDA, 0x9220),
        new(UsbBridgeKind.RealtekNvme, "Realtek 桥接（PID 未收录）", "sntrealtek", 0x0BDA, null),
        // ---- 老桥接（CDI [USB] 段里的那批）----
        new(UsbBridgeKind.Sunplus, "Sunplus 桥接", "usbsunplus", 0x04FC, null),
        new(UsbBridgeKind.Prolific, "Prolific 桥接", "usbprolific", 0x067B, null),
        new(UsbBridgeKind.Cypress, "Cypress 桥接", "usbcypress", 0x04B4, null),
        new(UsbBridgeKind.Iodata, "IODATA 桥接", "usbiodata", 0x04BB, null),
        new(UsbBridgeKind.Logitec, "Logitec 桥接", "usblogitec", 0x0789, null),
        // ---- 常见整机 / 品牌盒（走标准 SAT 即可）----
        new(UsbBridgeKind.Sat, "标准 SAT/UASP 桥接", "sat", 0x174C, null),
        new(UsbBridgeKind.Sat, "Samsung 移动硬盘盒", "sat", 0x04E8, null),
        new(UsbBridgeKind.Sat, "SanDisk/WD 移动硬盘", "sat", 0x1058, null),
        new(UsbBridgeKind.Sat, "Toshiba 移动硬盘", "sat", 0x0480, null),
        new(UsbBridgeKind.Sat, "VIA 桥接", "sat", 0x2109, null),
        new(UsbBridgeKind.Sat, "Initio 桥接", "sat", 0x13FD, null),
    ];

    /// <summary>自报字符串里的关键词（比 VID/PID 更准，优先匹配）。</summary>
    private static readonly (string Keyword, UsbBridgeKind Kind, string Name, string Hint)[] Text =
    [
        ("rtl9210", UsbBridgeKind.RealtekNvme, "Realtek RTL9210 (USB-NVMe)", "sntrealtek"),
        ("rtl9220", UsbBridgeKind.RealtekNvme, "Realtek RTL9220 (USB-NVMe)", "sntrealtek"),
        ("rtl9201", UsbBridgeKind.RealtekNvme, "Realtek RTL9201 (USB-SATA)", "sntrealtek"),
        ("asm2362", UsbBridgeKind.AsmediaNvme, "ASMedia ASM2362 (USB-NVMe)", "sntasmedia"),
        ("asm2364", UsbBridgeKind.AsmediaNvme, "ASMedia ASM2364 (USB-NVMe)", "sntasmedia"),
        ("asm1352", UsbBridgeKind.AsmediaDual, "ASMedia ASM1352R 双盘位", "usbasmedia"),
        ("asm1351", UsbBridgeKind.AsmediaSata, "ASMedia ASM1351 (USB-SATA)", "usbasmedia"),
        ("asm1153", UsbBridgeKind.AsmediaSata, "ASMedia ASM1153E (USB-SATA)", "usbasmedia"),
        ("asmedia", UsbBridgeKind.AsmediaSata, "ASMedia 桥接", "usbasmedia"),
        ("jms583", UsbBridgeKind.JmicronsNvme, "JMicron JMS583 (USB-NVMe)", "sntjmicron"),
        ("jms586", UsbBridgeKind.JmicronsNvme, "JMicron JMS586 (USB-NVMe)", "sntjmicron"),
        ("jms578", UsbBridgeKind.JmicronSata, "JMicron JMS578 (USB-SATA)", "usbjmicron"),
        ("jmb39", UsbBridgeKind.Jmb39x, "JMicron JMB39x 多盘位", "jmb39x"),
        ("jmicron", UsbBridgeKind.JmicronSata, "JMicron 桥接", "usbjmicron"),
        ("sunplus", UsbBridgeKind.Sunplus, "Sunplus 桥接", "usbsunplus"),
        ("prolific", UsbBridgeKind.Prolific, "Prolific 桥接", "usbprolific"),
        ("cypress", UsbBridgeKind.Cypress, "Cypress 桥接", "usbcypress"),
        ("initio", UsbBridgeKind.Sat, "Initio 桥接", "sat"),
        ("uas", UsbBridgeKind.Sat, "UASP 桥接", "sat"),
    ];

    public static BridgeIdentity Resolve(ushort? vid, ushort? pid, string? busReported, string? friendlyName)
    {
        string text = ((busReported ?? "") + " " + (friendlyName ?? "")).ToLowerInvariant();

        foreach (var (kw, kind, name, hint) in Text)
            if (text.Contains(kw, StringComparison.Ordinal))
                return new BridgeIdentity(kind, name, "自报字符串命中 \"" + kw + "\"", hint);

        if (vid is not null)
        {
            if (pid is not null)
            {
                var exact = Table.FirstOrDefault(e => e.Vid == vid && e.Pid == pid);
                if (exact is not null)
                    return new BridgeIdentity(exact.Kind, exact.Name, $"VID_{vid:X4}&PID_{pid:X4} 精确命中", exact.Hint);
            }
            var byVid = Table.FirstOrDefault(e => e.Vid == vid && e.Pid is null);
            if (byVid is not null)
                return new BridgeIdentity(byVid.Kind, byVid.Name, $"VID_{vid:X4} 命中厂商", byVid.Hint);
        }

        return new BridgeIdentity(UsbBridgeKind.Unknown, "未收录的桥接主控",
            "VID/PID 与自报字符串都未命中特征库", "sat");
    }
}
