using System.Diagnostics;
using GuDisk.Core.Devices;

namespace GuDisk.Core.Smart;

/// <summary>
/// 双引擎逐字段比对的一条。
/// 注意：**累计计数器在两次读取之间会自然递增**（系统盘尤其明显，130 ms 的间隔就能差十几）。
/// 因此计数器只校验"没有倒退且增量在容忍范围内"，状态量才要求严格相等。
/// </summary>
public sealed record CrossCheckItem(string Field, string Native, string Smartctl, bool Agree, string? Note = null);

public sealed record SmartReadOutcome(
    bool Success,
    string Engine,                 // Native | smartctl | Native+smartctl
    string? Error,
    NvmeSmartLog? Nvme,
    AtaSmartBlock? Ata,
    IdentityInfo? Identity,
    IReadOnlyList<SmartAttribute> Attributes,
    string Source,
    IReadOnlyList<ProbeAttempt> Attempts,
    IReadOnlyList<CrossCheckItem> CrossCheck);

/// <summary>
/// 双引擎编排：原生先跑（快）→ 失败转 smartctl → 两者都成功时逐字段比对。
/// 比对不通过的字段在 UI 上会被标"数据存疑"。
/// </summary>
public sealed class SmartReadOrchestrator
{
    private readonly SmartctlEngine _smartctl;

    public SmartReadOrchestrator(SmartctlEngine? smartctl = null) => _smartctl = smartctl ?? new SmartctlEngine();

    public SmartReadOutcome Read(PhysicalDriveInfo drive, bool alsoCrossCheck = true, string forceProtocol = "auto",
        string? deviceTypeOverride = null)
    {
        var attempts = new List<ProbeAttempt>();

        // ---- 层 1：原生 ----
        // **USB + NVMe 桥接盘直接跳过原生引擎**：盒子里是 NVMe 盘，根本没有 ATA 命令，
        // 发 ATA PASS-THROUGH 只会得到 Win32 87（ERROR_INVALID_FUNCTION）—— 不是权限问题，
        // 是命令本身就不该发。这条路只有 smartctl 的厂商私有协议（sntrealtek/sntjmicron）走得通。
        bool usbNvmeBridge = drive.IsUsb
            && (drive.SmartctlDeviceType?.StartsWith("snt", StringComparison.OrdinalIgnoreCase) == true
                || drive.SmartctlDeviceType?.Equals("nvme", StringComparison.OrdinalIgnoreCase) == true);
        SmartProbeResult native;
        if (usbNvmeBridge)
        {
            attempts.Add(new ProbeAttempt("Native",
                "跳过（USB-NVMe 桥接盘没有 ATA 命令，发 ATA PASS-THROUGH 必然 Win32 87）",
                false, null, "跳过：由 smartctl 走桥接私有协议", 0));
            native = new SmartProbeResult(
                Success: false, Error: "USB-NVMe 桥接盘由 smartctl 走桥接私有协议",
                Win32Error: 0, Source: "跳过", Nvme: null, Ata: null, Identity: null,
                Attributes: [], Transport: "跳过");
        }
        else
        {
            var sw = Stopwatch.StartNew();
            native = SmartProbe.Read(drive, forceProtocol);
            sw.Stop();
            attempts.Add(new ProbeAttempt("Native", NativeCommandOf(drive, forceProtocol), native.Success,
                native.Win32Error != 0 ? "Win32 " + native.Win32Error : null,
                native.Success ? null : native.Error, (int)sw.ElapsedMilliseconds));
        }

        // ---- 层 2：smartctl（原生失败时兜底；原生成功时可选做交叉校验）----
        SmartctlResult? sc = null;
        bool wantSmartctl = _smartctl.Available && (!native.Success || alsoCrossCheck);
        if (wantSmartctl)
        {
            var sw2 = Stopwatch.StartNew();
            sc = _smartctl.Read(drive, string.IsNullOrWhiteSpace(deviceTypeOverride) ? null : deviceTypeOverride);
            sw2.Stop();
            attempts.Add(new ProbeAttempt("smartctl",
                "smartctl -x -j -d " + (deviceTypeOverride ?? drive.SmartctlDeviceType ?? "auto") + " " + (sc.ResolvedDevice ?? "（设备未解析）"),
                sc.Success,
                sc.Parsed is null ? null : "exit " + sc.Parsed.ExitStatus,
                sc.Success ? null : sc.Error,
                (int)sw2.ElapsedMilliseconds));
        }

        // ---- 决策 ----
        if (native.Success && native.Nvme is not null)
        {
            var cross = sc?.Success == true && sc.Parsed?.Nvme is not null
                ? Compare(native.Nvme, sc.Parsed.Nvme)
                : [];
            return new SmartReadOutcome(true,
                cross.Count > 0 ? "Native + smartctl" : "Native",
                null, native.Nvme, native.Ata, native.Identity, native.Attributes,
                native.Source, attempts, cross);
        }

        // ATA 路径成功（SATA / 机械盘 / USB 盒）
        if (native.Success && native.Ata is not null)
            return new SmartReadOutcome(true, "Native", null, null, native.Ata, native.Identity,
                native.Attributes, native.Transport, attempts, []);

        if (sc?.Success == true && sc.Parsed is not null)
        {
            bool isNvme = sc.Parsed.Nvme is not null;
            var attrs = isNvme
                ? NvmeSmartReader.ToAttributes(sc.Parsed.Nvme!)
                : sc.Parsed.AtaAttributes;
            var ataBlock = !isNvme && sc.Parsed.AtaAttributes.Count > 0
                ? new AtaSmartBlock(sc.Parsed.SmartPassed, sc.Parsed.AtaAttributes, [])
                : null;
            // **回填真实身份**：USB 桥接盘在 Windows 眼里是"桥接芯片"（Realtek RTL9210B-CG），
            // smartctl 穿过桥读到的是盘自己（KIOXIA EXCERIA PLUS G3）。不回填的话，
            // 界面标题、历史目录名、DiskKey 全都挂在桥上 —— 换个盒子同一块盘就变成"另一块盘"了。
            // 通电时间(0x09) / 通电次数(0x0C)：ATA 盘只能从 SMART 属性取（NVMe 才有专门的健康日志字段）。
            // 以前这两个字段在 smartctl 路径上恒为 0，界面于是显示 "0 次" 和 "-"。
            long poh = 0, poc = 0;
            foreach (var a in sc.Parsed.AtaAttributes)
            {
                if (a.Id == 0x09) poh = (long)a.Raw;
                if (a.Id == 0x0C) poc = (long)a.Raw;
            }

            var identity = string.IsNullOrWhiteSpace(sc.Parsed.Model) ? null : new IdentityInfo(
                Model: sc.Parsed.Model, Serial: sc.Parsed.Serial, Firmware: sc.Parsed.Firmware,
                Standard: sc.Parsed.Nvme is not null ? "NVMe" : "ATA",
                Interface: drive.IsUsb ? "USB" : (sc.Parsed.Nvme is not null ? "PCIe" : "SATA"),
                // 链路速率：USB-SATA 桥接盘从 IDENTIFY word 76 得到（"SATA/600"），
                // 拿不到就退回 "-"，不编。
                TransferMode: sc.Parsed.SataSpeed ?? "-", SupportedMode: sc.Parsed.SataSpeed ?? "-",
                RotationRateRpm: sc.Parsed.RotationRateRpm, PowerOnHours: poh, PowerOnCount: poc,
                TrimSupported: false, ZonedDevice: false, NcQSupported: false,
                Capacities: null, FormFactor: "未报告");

            return new SmartReadOutcome(true, "smartctl", null,
                sc.Parsed.Nvme, ataBlock, identity, attrs,
                "smartctl " + sc.Parsed.DeviceName + " (" + sc.Parsed.DeviceType + ")",
                attempts, []);
        }

        return new SmartReadOutcome(false, native.Success ? "Native" : "none",
            native.Error ?? sc?.Error ?? "两个引擎都未能读取", null, null, null, [], "", attempts, []);
    }

    private static string NativeCommandOf(PhysicalDriveInfo d, string forceProtocol) => forceProtocol switch
    {
        "ata" or "sat" => "IOCTL_ATA_PASS_THROUGH_DIRECT(SMART READ DATA 0xB0) → IOCTL_SCSI_PASS_THROUGH_DIRECT(ATA PASS-THROUGH 16, CDB 0x85)",
        "scsi" => "IOCTL_SCSI_PASS_THROUGH_DIRECT(ATA PASS-THROUGH 16, CDB 0x85)",
        "nvme" => "IOCTL_STORAGE_QUERY_PROPERTY(StorageDeviceProtocolSpecificProperty, LogPage 0x02)",
        _ => d.Protocol switch
        {
            StorageProtocolKind.Nvme => "IOCTL_STORAGE_QUERY_PROPERTY(StorageDeviceProtocolSpecificProperty, LogPage 0x02)",
            StorageProtocolKind.Ata or StorageProtocolKind.Sata => "IOCTL_ATA_PASS_THROUGH_DIRECT(SMART READ DATA 0xB0)",
            _ => "IOCTL_SCSI_PASS_THROUGH_DIRECT(ATA PASS-THROUGH 16, CDB 0x85)"
        }
    };

    /// <summary>逐字段比对。状态量严格相等；累计计数器允许间隔内递增；温度允许 ±3 ℃。</summary>
    public static IReadOnlyList<CrossCheckItem> Compare(NvmeSmartLog a, NvmeSmartLog b)
    {
        var list = new List<CrossCheckItem>();
        void Add(string f, string x, string y, bool agree, string? note = null)
            => list.Add(new CrossCheckItem(f, x, y, agree, note));

        // 状态量：必须严格相等
        void State(string f, ulong x, ulong y) => Add(f, x.ToString(), y.ToString(), x == y);

        // 累计计数器：不能倒退；增量在容忍范围内即可（默认 max(1000, 0.1%)）
        void Counter(string f, ulong x, ulong y)
        {
            ulong tol = Math.Max(1000UL, x / 1000UL);
            if (y < x) { Add(f, x.ToString(), y.ToString(), false, "smartctl 读数更小（异常）"); return; }
            ulong delta = y - x;
            if (delta == 0) { Add(f, x.ToString(), y.ToString(), true); return; }
            Add(f, x.ToString(), y.ToString(), delta <= tol, "读取间隔内 +" + delta);
        }

        State("严重警告标志", a.CriticalWarning, b.CriticalWarning);
        State("可用备用空间", a.AvailableSpare, b.AvailableSpare);
        State("可用备用空间阈值", a.AvailableSpareThreshold, b.AvailableSpareThreshold);
        State("已用寿命百分比", a.PercentageUsed, b.PercentageUsed);
        State("介质与数据完整性错误", a.MediaErrors, b.MediaErrors);
        State("错误日志项数", a.ErrorLogEntries, b.ErrorLogEntries);

        Counter("读取单位计数", a.DataUnitsRead, b.DataUnitsRead);
        Counter("写入单位计数", a.DataUnitsWritten, b.DataUnitsWritten);
        Counter("主机读命令计数", a.HostReadCommands, b.HostReadCommands);
        Counter("主机写命令计数", a.HostWriteCommands, b.HostWriteCommands);
        Counter("控制器忙状态时间", a.ControllerBusyTime, b.ControllerBusyTime);
        Counter("启动-关闭循环次数", a.PowerCycles, b.PowerCycles);
        Counter("通电时间", a.PowerOnHours, b.PowerOnHours);
        Counter("不安全关机计数", a.UnsafeShutdowns, b.UnsafeShutdowns);

        // 温度是**活值**：两次读取之间隔着一个 smartctl 进程（约 200 ms），
        // 笔记本上负载一变就能跳好几度。原来容差 ±1 K 太紧 —— 实测撞上 40 ℃ | 38 ℃（差 2 K）
        // 就报"1 项不一致"，读起来像两个引擎打架，其实只是温度在动。
        // 放到 ±3 K：真的解析错会是几十 K 的差（或者压根是 0/273），照样抓得住。
        int ta = a.CompositeTemperatureK, tb = b.CompositeTemperatureK;
        int dt = Math.Abs(ta - tb);
        Add("综合温度", (ta - 273) + " ℃", (tb - 273) + " ℃", dt <= 3,
            dt == 0 ? null : "读取间隔内变化 " + dt + " ℃");
        return list;
    }
}
