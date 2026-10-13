using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static GuDisk.Core.Interop.NativeMethods;
using static GuDisk.Core.Interop.StorageStructures;

namespace GuDisk.Core.Interop;

/// <summary>DeviceIoControl 的薄封装：结构体 ↔ 字节缓冲、查询属性、取设备号与几何。</summary>
internal static class DeviceIo
{
    /// <summary>打开设备。查询类调用用 access=0（非管理员也能开），SMART 透传才需要读写权限。</summary>
    public static SafeFileHandle Open(string path, uint access = 0)
        => CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    public static bool Ioctl(SafeFileHandle h, uint code,
        byte[]? inBuf, int inLen, byte[]? outBuf, out int returned, out int win32Error)
    {
        IntPtr pin = IntPtr.Zero, pout = IntPtr.Zero;
        int outLen = outBuf?.Length ?? 0;
        try
        {
            if (inBuf is not null && inLen > 0)
            {
                pin = Marshal.AllocHGlobal(inLen);
                Marshal.Copy(inBuf, 0, pin, inLen);
            }
            if (outLen > 0) pout = Marshal.AllocHGlobal(outLen);

            bool ok = DeviceIoControl(h, code, pin, inLen, pout, outLen, out returned, IntPtr.Zero);
            win32Error = ok ? 0 : Marshal.GetLastWin32Error();
            if (ok && outLen > 0 && returned > 0)
                Marshal.Copy(pout, outBuf!, 0, Math.Min(returned, outLen));
            return ok;
        }
        finally
        {
            if (pin != IntPtr.Zero) Marshal.FreeHGlobal(pin);
            if (pout != IntPtr.Zero) Marshal.FreeHGlobal(pout);
        }
    }

    public static byte[] StructToBytes<T>(T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        var buf = new byte[size];
        IntPtr p = Marshal.AllocHGlobal(size);
        try { Marshal.StructureToPtr(value, p, false); Marshal.Copy(p, buf, 0, size); }
        finally { Marshal.FreeHGlobal(p); }
        return buf;
    }

    public static T BytesToStruct<T>(byte[] buf, int offset = 0) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr p = Marshal.AllocHGlobal(size);
        try { Marshal.Copy(buf, offset, p, size); return Marshal.PtrToStructure<T>(p); }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>IOCTL_STORAGE_QUERY_PROPERTY 的通用调用。</summary>
    public static byte[]? QueryProperty(SafeFileHandle h, StoragePropertyId id, int bufferSize, byte[]? extraInput = null)
    {
        var q = new STORAGE_PROPERTY_QUERY { PropertyId = (int)id, QueryType = (int)StorageQueryType.PropertyStandardQuery };
        byte[] qb = StructToBytes(q);
        if (extraInput is not null)
        {
            var merged = new byte[qb.Length + extraInput.Length];
            qb.CopyTo(merged, 0);
            extraInput.CopyTo(merged, qb.Length);
            qb = merged;
        }
        var outBuf = new byte[bufferSize];
        if (Ioctl(h, IOCTL_STORAGE_QUERY_PROPERTY, qb, qb.Length, outBuf, out int ret, out _) && ret > 0)
        {
            Array.Resize(ref outBuf, ret);
            return outBuf;
        }
        return null;
    }

    /// <summary>
    /// 协议特定属性查询（NVMe Identify / LogPage、ATA 等）。返回协议数据段，失败返回 null。
    ///
    /// 输入缓冲布局（踩过 Win32 87 才定下来的）：
    ///   [0..4) PropertyId  [4..8) QueryType  [8..) STORAGE_PROTOCOL_SPECIFIC_DATA
    /// 即协议数据从偏移 8 开始（&amp;AdditionalParameters[0]），不是 12；
    /// 输入长度按 winioctl.h 惯例取 sizeof(QUERY) + sizeof(PSD) - 1。
    /// </summary>
    public static byte[]? QueryProtocolSpecific(SafeFileHandle h, StoragePropertyId propId,
        ProtocolType protocol, int dataType, int requestValue, int dataLength, int requestSubValue = 0)
    {
        var psd = new STORAGE_PROTOCOL_SPECIFIC_DATA
        {
            ProtocolType = (int)protocol,
            DataType = dataType,
            ProtocolDataRequestValue = requestValue,
            ProtocolDataRequestSubValue = requestSubValue,
            ProtocolDataOffset = Marshal.SizeOf<STORAGE_PROTOCOL_SPECIFIC_DATA>(),
            ProtocolDataLength = dataLength,
        };
        byte[] psdBytes = StructToBytes(psd);

        var input = new byte[12 + psdBytes.Length];
        BitConverter.GetBytes((int)propId).CopyTo(input, 0);
        BitConverter.GetBytes((int)StorageQueryType.PropertyStandardQuery).CopyTo(input, 4);
        psdBytes.CopyTo(input, 8);
        int inLen = 12 + psdBytes.Length - 1;

        int descSize = Marshal.SizeOf<STORAGE_PROTOCOL_DATA_DESCRIPTOR>();
        var outBuf = new byte[descSize + dataLength];
        if (!Ioctl(h, IOCTL_STORAGE_QUERY_PROPERTY, input, inLen, outBuf, out int ret, out _) || ret < descSize)
            return null;

        var desc = BytesToStruct<STORAGE_PROTOCOL_DATA_DESCRIPTOR>(outBuf);
        int off = desc.ProtocolSpecificData.ProtocolDataOffset;
        int len = desc.ProtocolSpecificData.ProtocolDataLength;
        if (off < descSize) off = descSize;
        if (len <= 0 || off + len > outBuf.Length) len = Math.Min(dataLength, outBuf.Length - off);
        if (len <= 0) return null;

        var data = new byte[len];
        Array.Copy(outBuf, off, data, 0, len);
        return data;
    }

    /// <summary>
    /// 下发一条 NVMe Admin 命令（IOCTL_STORAGE_PROTOCOL_COMMAND）。
    /// 结构布局：STORAGE_PROTOCOL_COMMAND 头 84 字节（Command[] 在偏移 84），随后是 64 字节 NVMe 命令，
    /// 再往后是设备返回的数据区（DataFromDeviceBufferOffset = 84 + 64）。
    /// </summary>
    public static bool SendNvmeCommand(SafeFileHandle h, byte[] command64, int dataFromDeviceLength,
        out int returnStatus, out int win32Error, int timeoutSeconds = 10)
    {
        const int headerSize = 84;
        returnStatus = 0;
        int cmdLen = command64.Length;
        int dataOffset = headerSize + cmdLen;
        int inSize = dataOffset;
        int outSize = dataOffset + Math.Max(0, dataFromDeviceLength);

        var input = new byte[inSize];
        BitConverter.GetBytes((uint)headerSize).CopyTo(input, 0);                  // Version
        BitConverter.GetBytes((uint)headerSize).CopyTo(input, 4);                  // Length
        BitConverter.GetBytes((int)ProtocolType.Nvme).CopyTo(input, 8);            // ProtocolType
        BitConverter.GetBytes(0u).CopyTo(input, 12);                               // Flags
        BitConverter.GetBytes((uint)cmdLen).CopyTo(input, 28);                     // CommandLength
        BitConverter.GetBytes((uint)Math.Max(0, dataFromDeviceLength)).CopyTo(input, 40); // DataFromDeviceTransferLength
        BitConverter.GetBytes((uint)timeoutSeconds).CopyTo(input, 44);             // TimeOutValue
        BitConverter.GetBytes((uint)dataOffset).CopyTo(input, 56);                 // DataFromDeviceBufferOffset
        command64.CopyTo(input, headerSize);

        var output = new byte[outSize];
        bool ok = Ioctl(h, IOCTL_STORAGE_PROTOCOL_COMMAND, input, inSize, output, out _, out win32Error);
        if (ok) returnStatus = BitConverter.ToInt32(output, 16);                   // ReturnStatus
        return ok;
    }

    public static uint? GetDeviceNumber(SafeFileHandle h)
    {
        var outBuf = new byte[Marshal.SizeOf<STORAGE_DEVICE_NUMBER>()];
        if (Ioctl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, null, 0, outBuf, out _, out _))
            return BytesToStruct<STORAGE_DEVICE_NUMBER>(outBuf).DeviceNumber;
        return null;
    }

    public static (ulong Size, uint BytesPerSector)? GetGeometry(SafeFileHandle h)
    {
        var outBuf = new byte[Marshal.SizeOf<DISK_GEOMETRY_EX>() + 64];
        if (Ioctl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 0, outBuf, out _, out _))
        {
            var g = BytesToStruct<DISK_GEOMETRY_EX>(outBuf);
            return ((ulong)g.DiskSize, g.Geometry.BytesPerSector);
        }
        return null;
    }

    /// <summary>从 STORAGE_DEVICE_DESCRIPTOR 的偏移字段读 ANSI 字符串。</summary>
    public static string ReadAnsiString(byte[] buf, uint offset)
    {
        if (offset == 0 || offset >= buf.Length) return "";
        int end = (int)offset;
        while (end < buf.Length && buf[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(buf, (int)offset, end - (int)offset).Trim();
    }
}
