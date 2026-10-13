using System.Text;
using static GuDisk.Core.Interop.NativeMethods;

namespace GuDisk.Core.Devices;

/// <summary>
/// USB 桥接识别：沿设备树向上找到 USB 枚举器，取 VID/PID 与主控自报字符串。
/// 入参直接用 DEVINST（由 SetupDiGetDeviceInterfaceDetailW 顺带取回），不需要再把实例 ID 反查成节点。
/// </summary>
internal static class UsbBridgeDetector
{
    public sealed record UsbAncestry(ushort? Vid, ushort? Pid, string? BusReported, string? UsbInstanceId);

    public static UsbAncestry? Walk(uint devInst)
    {
        uint node = devInst;
        for (int depth = 0; depth < 12; depth++)
        {
            if (CM_Get_Parent(out uint parent, node, 0) != CR_SUCCESS) return null;
            node = parent;

            string id = GetDeviceId(node);
            if (id.Length == 0) return null;

            if (id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
            {
                var (vid, pid) = ParseVidPid(id);
                return new UsbAncestry(vid, pid, GetBusReportedDesc(node), id);
            }
        }
        return null;
    }

    /// <summary>向上找到 PCI 控制器节点（判断盘挂在哪个控制器上）。</summary>
    public static string FindAdapter(uint devInst)
    {
        uint node = devInst;
        for (int i = 0; i < 10; i++)
        {
            if (CM_Get_Parent(out uint parent, node, 0) != CR_SUCCESS) break;
            node = parent;
            string id = GetDeviceId(node);
            if (id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
            {
                var parts = id.Split('&');
                return parts.Length >= 2 ? parts[0] + "&" + parts[1] : id;
            }
        }
        return "";
    }

    public static string GetDeviceId(uint node)
    {
        var buf = new char[512];
        if (CM_Get_Device_IDW(node, buf, buf.Length, 0) != CR_SUCCESS) return "";
        int len = Array.IndexOf(buf, '\0');
        if (len < 0) len = buf.Length;
        return new string(buf, 0, len);
    }

    private static (ushort? Vid, ushort? Pid) ParseVidPid(string instanceId)
    {
        ushort? vid = null, pid = null;
        int vi = instanceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        if (vi >= 0 && vi + 8 <= instanceId.Length &&
            ushort.TryParse(instanceId.AsSpan(vi + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out var v))
            vid = v;
        int pi = instanceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (pi >= 0 && pi + 8 <= instanceId.Length &&
            ushort.TryParse(instanceId.AsSpan(pi + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out var p))
            pid = p;
        return (vid, pid);
    }

    /// <summary>DEVPKEY_Device_BusReportedDeviceDesc —— 桥接主控自报字符串（如 RTL9210B-CG）。</summary>
    private static string? GetBusReportedDesc(uint node)
    {
        var key = DEVPKEY_Device_BusReportedDeviceDesc;
        uint size = 0;
        int rc = CM_Get_DevNode_PropertyW(node, ref key, out uint type, null, ref size, 0);
        if (rc != CR_SUCCESS || size == 0 || size > 4096) return null;

        var buf = new byte[size];
        rc = CM_Get_DevNode_PropertyW(node, ref key, out type, buf, ref size, 0);
        if (rc != CR_SUCCESS) return null;

        string all = Encoding.Unicode.GetString(buf, 0, (int)size);
        var first = all.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        first = first?.Trim();
        return string.IsNullOrEmpty(first) ? null : first;
    }
}
