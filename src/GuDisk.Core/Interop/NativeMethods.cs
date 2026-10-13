using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GuDisk.Core.Interop;

/// <summary>Win32 / SetupAPI / CfgMgr32 / 内核 IOCTL 的原始声明。只放声明，不做业务判断。</summary>
internal static class NativeMethods
{
    // ---------- 路径与常量 ----------
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;

    public const uint DIGCF_PRESENT = 0x2;
    public const uint DIGCF_DEVICEINTERFACE = 0x10;

    public const uint SPDRP_DEVICEDESC = 0x0;
    public const uint SPDRP_HARDWAREID = 0x1;
    public const uint SPDRP_FRIENDLYNAME = 0xC;
    public const uint SPDRP_PHYSICAL_DEVICE_OBJECT_NAME = 0xE;
    public const uint SPDRP_ENUMERATOR_NAME = 0x16;

    public const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    public const int CR_SUCCESS = 0;
    public const uint DEVPROP_TYPE_STRING = 0x00000012;
    public const uint DEVPROP_TYPE_STRING_LIST = 0x00002012;

    // ---------- CTL_CODE ----------
    private const uint FILE_DEVICE_DISK = 0x07;
    private const uint FILE_DEVICE_CONTROLLER = 0x04;
    private const uint FILE_DEVICE_MASS_STORAGE = 0x2D;
    private const uint METHOD_BUFFERED = 0;
    private const uint FILE_ANY_ACCESS = 0;
    private const uint FILE_READ_ACCESS = 1;
    private const uint FILE_WRITE_ACCESS = 2;
    private const uint FILE_RW_ACCESS = FILE_READ_ACCESS | FILE_WRITE_ACCESS;

    /// <summary>与 winioctl.h 的 CTL_CODE 宏等价；数值一律按此计算，避免手抄常量抄错。</summary>
    private static uint Ctl(uint devType, uint func, uint method, uint access)
        => (devType << 16) | (access << 14) | (func << 2) | method;

    public static readonly uint IOCTL_STORAGE_QUERY_PROPERTY       = Ctl(FILE_DEVICE_MASS_STORAGE, 0x0500, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint IOCTL_STORAGE_GET_DEVICE_NUMBER    = Ctl(FILE_DEVICE_MASS_STORAGE, 0x0420, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX   = Ctl(FILE_DEVICE_DISK, 0x0028, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX     = Ctl(FILE_DEVICE_DISK, 0x0014, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint IOCTL_ATA_PASS_THROUGH_DIRECT      = Ctl(FILE_DEVICE_CONTROLLER, 0x0405, METHOD_BUFFERED, FILE_RW_ACCESS);
    public static readonly uint IOCTL_SCSI_PASS_THROUGH_DIRECT     = Ctl(FILE_DEVICE_CONTROLLER, 0x0401, METHOD_BUFFERED, FILE_RW_ACCESS);
    public static readonly uint IOCTL_STORAGE_PROTOCOL_COMMAND     = Ctl(FILE_DEVICE_MASS_STORAGE, 0x0F00, METHOD_BUFFERED, FILE_RW_ACCESS);

    // ---------- SetupAPI ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    /// <summary>SetupAPI 的设备信息数据（注意：与 SP_DEVICE_INTERFACE_DATA 不是一回事）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize, out int requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData, [Out] char[] deviceInstanceId,
        int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, uint property,
        out uint propertyRegDataType, byte[]? propertyBuffer, int propertyBufferSize, out int requiredSize);

    // ---------- CfgMgr32 ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_IDW(uint dnDevInst, char[] buffer, int bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey,
        out uint propertyType, byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    /// <summary>DEVPKEY_Device_BusReportedDeviceDesc —— 桥接主控自报字符串（如 RTL9210B-CG）。</summary>
    public static DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc => new()
    {
        fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"),
        pid = 4
    };

    // ---------- 内核对象 ----------
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
        IntPtr inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    // ---------- 设备接口类 ----------
    /// <summary>GUID_DEVINTERFACE_DISK = {53F56307-B6BF-11D0-94F2-00A0C91EFB8B}</summary>
    public static Guid GUID_DEVINTERFACE_DISK =>
        new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
}
