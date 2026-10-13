using System.Diagnostics;
using System.Text.Json;
using GuDisk.Core.Devices;

namespace GuDisk.Core.Smart;

public sealed record SmartctlParsed(
    string Model, string Serial, string Firmware,
    bool SmartPassed, bool SmartPassedKnown,
    NvmeSmartLog? Nvme,
    IReadOnlyList<SmartAttribute> AtaAttributes,
    string DeviceName, string DeviceType, int ExitStatus,
    IReadOnlyList<string> Messages);

public sealed record SmartctlResult(
    bool Success, string? Error, SmartctlParsed? Parsed, string RawJson,
    /// <summary>解析到的 /dev/sdX。**失败路径也要填** —— 否则「解析失败」和「读失败」在界面上长得一样。</summary>
    string? ResolvedDevice = null);

/// <summary>
/// 层 2 引擎：调用随包 smartctl 的 `-x -j` 拿 JSON。
///
/// 三个实测要点（第三条是接上真实硬盘盒后才发现）：
///  ① Windows 下 smartctl 认的路径是 `/dev/sdX`，不是 `\\.\\PhysicalDriveN`（后者连同 -d 一起报 "Invalid argument"）；
///  ② `--scan` 的枚举顺序不保证与 PhysicalDriveN 一致 —— 所以靠 **序列号匹配** 定位设备，不靠顺序；
///  ③ **序列号匹配对 USB 硬盘盒永远不会命中** —— Windows 报的是桥接芯片的序列号，
///     而 smartctl 穿过桥读的是盘自己的序列号，两者天生不同。所以加了"按 -d 类型匹配"这一路。
/// </summary>
public sealed class SmartctlEngine
{
    private readonly string _exe;
    private Dictionary<string, string>? _serialToDevice;   // serial(大写) → /dev/sdX
    private List<(string Name, string Type)>? _scanCache;  // --scan 结果（含 -d 类型）

    public SmartctlEngine(string? smartctlExe = null)
    {
        _exe = smartctlExe ?? Path.Combine(Core.App.AppPaths.FindSmartctlDir() ?? "", "smartctl.exe");
    }

    public bool Available => File.Exists(_exe);

    public SmartctlResult Read(PhysicalDriveInfo drive, string? deviceTypeOverride = null)
    {
        if (!Available)
            return new SmartctlResult(false, "未找到 smartctl：" + _exe, null, "");

        string? dev = ResolveDevice(drive);
        if (dev is null)
            return new SmartctlResult(false,
                "smartctl 没能定位到这块盘（--scan 找到 " + Scan().Count + " 个设备，无法与序列号 \"" + drive.Serial
                + "\" 或类型 \"" + (drive.SmartctlDeviceType ?? "auto") + "\" 对应）。"
                + (drive.IsUsb ? "USB 硬盘盒需要管理员权限才能穿过桥读取。" : ""), null, "");

        string type = deviceTypeOverride ?? drive.SmartctlDeviceType ?? "auto";
        string args = "-x -j -d " + type + " " + dev;
        var (stdout, _, exit) = Run(args);
        if (string.IsNullOrWhiteSpace(stdout))
            return new SmartctlResult(false, "smartctl 无输出（" + dev + "）", null, "");

        try
        {
            var parsed = Parse(stdout, exit);
            return parsed is null
                ? new SmartctlResult(false, "smartctl JSON 解析失败（" + dev + "）", null, stdout, dev)
                : new SmartctlResult(true, null, parsed, stdout, dev);
        }
        catch (Exception ex)
        {
            return new SmartctlResult(false, "smartctl 输出解析异常: " + ex.Message, null, stdout);
        }
    }

    /// <summary>
    /// 解析 `--scan` 的一行：`/dev/sdc -d sntrealtek # /dev/sdc [USB NVMe Realtek], NVMe device`。
    ///
    /// **类型必须留下来** —— 原来只取设备名、把 " -d sntrealtek" 整段丢了，
    /// 于是后面不带 -d 去读序列号，USB 桥接盘必然读不到（smartctl 自动探测对桥接盘不可靠）。
    /// </summary>
    private static (string Name, string Type)? ParseScanLine(string line)
    {
        int hash = line.IndexOf('#');
        string body = (hash >= 0 ? line[..hash] : line).Trim();
        if (!body.StartsWith("/dev/", StringComparison.Ordinal)) return null;
        var parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        string type = "auto";
        for (int i = 1; i + 1 < parts.Length; i++)
            if (parts[i] == "-d") { type = parts[i + 1]; break; }
        return (parts[0], type);
    }

    private List<(string Name, string Type)> Scan()
    {
        if (_scanCache is not null) return _scanCache;
        _scanCache = [];
        var (text, _, _) = Run("--scan");
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (ParseScanLine(line) is { } e) _scanCache.Add(e);
        return _scanCache;
    }

    /// <summary>定位 /dev/sdX。三级策略，命中即返回。</summary>
    private string? ResolveDevice(PhysicalDriveInfo drive)
    {
        var devices = Scan();
        if (devices.Count == 0) return null;

        // ① 序列号精确匹配（首选）。读序列号时**必须带上 --scan 给的类型**
        if (_serialToDevice is null)
        {
            _serialToDevice = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, type) in devices)
            {
                var (info, _, _) = Run("-i -j -d " + type + " " + name);
                string serial = ExtractString(info, "serial_number");
                if (serial.Length > 0) _serialToDevice[serial] = name;
            }
        }
        if (_serialToDevice.TryGetValue(drive.Serial, out var hit)) return hit;

        // ② 按 smartctl 认出的 -d 类型匹配 —— 这一路专门为 USB 硬盘盒而加。
        //    Windows 报的是桥接芯片的序列号，smartctl 读的是盘自己的，两者天生不同，
        //    所以序列号匹配对硬盘盒永远不会命中。
        //    **只在同类型设备恰好唯一时才敢用**（本机两块 NVMe 就都是 nvme，会走到 ③）。
        string want = drive.SmartctlDeviceType ?? "";
        if (want.Length > 0 && !want.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var same = new List<string>();
            foreach (var (name, type) in devices)
                if (type.Equals(want, StringComparison.OrdinalIgnoreCase)) same.Add(name);
            if (same.Count == 1) return same[0];
        }

        // ③ 兜底：按设备号猜 /dev/sd{字母}（只在枚举顺序恰好一致时成立，且必须序列号对得上）
        char letter = (char)('a' + Math.Clamp(drive.DeviceNumber, 0, 25));
        string guess = "/dev/sd" + letter;
        var (probe, _, _) = Run("-i -j " + guess);
        string probedSerial = ExtractString(probe, "serial_number");
        if (probedSerial.Length > 0 && probedSerial.Equals(drive.Serial, StringComparison.OrdinalIgnoreCase))
            return guess;
        return null;
    }

    private static string ExtractString(string json, string key)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "").Trim() : "";
        }
        catch { return ""; }
    }

    private (string Stdout, string Stderr, int Exit) Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo(_exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return ("", "启动失败", -1);
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return (o, "超时", -1); }
            return (o, e, p.ExitCode);
        }
        catch (Exception ex) { return ("", ex.Message, -1); }
    }

    // ---------------- JSON → 我们的模型 ----------------
    public static SmartctlParsed? Parse(string json, int exitStatus)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("model_name", out _) && !root.TryGetProperty("device", out _)) return null;

        var messages = new List<string>();
        if (root.TryGetProperty("smartctl", out var sc) && sc.TryGetProperty("messages", out var msgs)
            && msgs.ValueKind == JsonValueKind.Array)
            foreach (var m in msgs.EnumerateArray())
                if (m.TryGetProperty("string", out var s)) messages.Add(s.GetString() ?? "");

        string model = Str(root, "model_name");
        string serial = Str(root, "serial_number");
        string firmware = Str(root, "firmware_version");
        string devName = "", devType = "";
        if (root.TryGetProperty("device", out var dev))
        {
            devName = Str(dev, "name");
            devType = Str(dev, "type");
        }

        bool passedKnown = false, passed = false;
        if (root.TryGetProperty("smart_status", out var st) && st.TryGetProperty("passed", out var pv))
        {
            passedKnown = pv.ValueKind is JsonValueKind.True or JsonValueKind.False;
            passed = pv.ValueKind == JsonValueKind.True;
        }

        NvmeSmartLog? nvme = null;
        if (root.TryGetProperty("nvme_smart_health_information_log", out var nlog))
            nvme = FromSmartctlNvme(nlog);

        var ata = new List<SmartAttribute>();
        if (root.TryGetProperty("ata_smart_attributes", out var ataRoot)
            && ataRoot.TryGetProperty("table", out var table) && table.ValueKind == JsonValueKind.Array)
            foreach (var row in table.EnumerateArray())
                ata.Add(FromSmartctlAta(row));

        return new SmartctlParsed(model, serial, firmware, passed, passedKnown, nvme, ata, devName, devType, exitStatus, messages);
    }

    private static SmartAttribute FromSmartctlAta(JsonElement row)
    {
        byte id = (byte)Int(row, "id");
        string name = Str(row, "name");
        int value = Int(row, "value"), worst = Int(row, "worst"), thresh = Int(row, "thresh");
        ulong raw = 0; string rawStr = "";
        if (row.TryGetProperty("raw", out var r))
        {
            if (r.TryGetProperty("value", out var rv) && rv.ValueKind == JsonValueKind.Number) raw = rv.GetUInt64();
            rawStr = Str(r, "string");
        }
        bool failing = row.TryGetProperty("when_failed", out var wf) && !string.IsNullOrEmpty(Str(row, "when_failed"));
        string status = failing ? "危险" : (thresh > 0 && value <= thresh ? "注意" : "正常");
        string category = id is 5 or 196 or 197 or 198 or 0xBB ? "坏道"
                        : id is 9 or 12 or 0xA9 or 0xE7 ? "寿命"
                        : id is 194 or 190 or 0xBE ? "环境"
                        : id is 199 or 0xC7 ? "接口" : "统计";
        return new SmartAttribute(id, name, name, value, worst, thresh, raw, rawStr, rawStr, status, category);
    }

    private static NvmeSmartLog? FromSmartctlNvme(JsonElement n)
    {
        short tempC = (short)Int(n, "temperature");
        var sensors = new List<short>();
        if (n.TryGetProperty("temperature_sensors", out var ts) && ts.ValueKind == JsonValueKind.Array)
            foreach (var t in ts.EnumerateArray())
                if (t.ValueKind == JsonValueKind.Number) sensors.Add((short)(t.GetInt32() + 273));

        return new NvmeSmartLog(
            CriticalWarning: (byte)Int(n, "critical_warning"),
            CompositeTemperatureK: (short)(tempC + 273),
            AvailableSpare: (byte)Int(n, "available_spare"),
            AvailableSpareThreshold: (byte)Int(n, "available_spare_threshold"),
            PercentageUsed: (byte)Int(n, "percentage_used"),
            DataUnitsRead: U64(n, "data_units_read"),
            DataUnitsWritten: U64(n, "data_units_written"),
            HostReadCommands: U64(n, "host_reads"),
            HostWriteCommands: U64(n, "host_writes"),
            ControllerBusyTime: U64(n, "controller_busy_time"),
            PowerCycles: U64(n, "power_cycles"),
            PowerOnHours: U64(n, "power_on_hours"),
            UnsafeShutdowns: U64(n, "unsafe_shutdowns"),
            MediaErrors: U64(n, "media_errors"),
            ErrorLogEntries: U64(n, "num_err_log_entries"),
            WarningTempTimeMinutes: (uint)Int(n, "warning_temp_time"),
            CriticalTempTimeMinutes: (uint)Int(n, "critical_comp_time"),
            TemperatureSensors: sensors,
            ThermalTransition1: 0, ThermalTransition2: 0,
            ThermalTime1Minutes: 0, ThermalTime2Minutes: 0,
            Raw512: []);
    }

    private static string Str(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    private static int Int(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? (int)l : 0;

    private static ulong U64(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out ulong l) ? l : 0;
}
