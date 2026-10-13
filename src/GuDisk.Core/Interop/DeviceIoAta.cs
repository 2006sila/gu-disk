using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static GuDisk.Core.Interop.NativeMethods;
using static GuDisk.Core.Interop.AtaStructures;

namespace GuDisk.Core.Interop;

/// <summary>ATA / SCSI 透传的低层调用。走的是需要读写权限的设备句柄（必须管理员）。</summary>
internal static class DeviceIoAta
{
    /// <summary>一条 ATA 任务文件命令。dataIn=true 表示设备→主机（读）。</summary>
    public sealed record AtaCommand(IDEREGS Task, bool DataIn, int DataLength);

    /// <summary>用 IOCTL_ATA_PASS_THROUGH_DIRECT 发一条命令（直连 AHCI/IDE 的盘）。</summary>
    public static bool AtaPassThrough(SafeFileHandle h, AtaCommand cmd, byte[]? dataOut,
        out byte[]? dataIn, out int win32Error, int timeoutSeconds = 5)
    {
        dataIn = null;
        int dataLen = cmd.DataLength;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (dataLen > 0)
            {
                buffer = Marshal.AllocHGlobal(dataLen);
                if (cmd.DataIn) Marshal.Copy(new byte[dataLen], 0, buffer, dataLen);
                else if (dataOut is not null) Marshal.Copy(dataOut, 0, buffer, Math.Min(dataLen, dataOut.Length));
            }

            var apt = new ATA_PASS_THROUGH_DIRECT
            {
                Length = (ushort)Marshal.SizeOf<ATA_PASS_THROUGH_DIRECT>(),
                AtaFlags = (ushort)(ATA_FLAGS_DRDY_REQUIRED | (cmd.DataIn ? ATA_FLAGS_DATA_IN : ATA_FLAGS_DATA_OUT)),
                PathId = 0, TargetId = 0, Lun = 0,
                DataTransferLength = (uint)dataLen,
                TimeOutValue = (uint)timeoutSeconds,
                DataBuffer = buffer,
                PreviousTaskFile = default,
                CurrentTaskFile = cmd.Task,
            };
            if (dataLen == 0) apt.AtaFlags = ATA_FLAGS_DRDY_REQUIRED;

            byte[] inBytes = DeviceIo.StructToBytes(apt);
            var outBytes = new byte[Marshal.SizeOf<ATA_PASS_THROUGH_DIRECT>() + 64];
            bool ok = DeviceIo.Ioctl(h, IOCTL_ATA_PASS_THROUGH_DIRECT, inBytes, inBytes.Length, outBytes, out int ret, out win32Error);

            if (ok && dataLen > 0 && buffer != IntPtr.Zero)
            {
                dataIn = new byte[dataLen];
                Marshal.Copy(buffer, dataIn, 0, dataLen);
            }
            return ok;
        }
        catch (Exception)
        {
            win32Error = -1;
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>用 IOCTL_SCSI_PASS_THROUGH_DIRECT 发一条 ATA PASS-THROUGH(16)（USB 盒 / RAID 接管后的主路径）。</summary>
    public static bool ScsiAtaPassThrough16(SafeFileHandle h, byte[] cdb, int dataLength, bool dataIn,
        byte[]? dataOut, out byte[]? dataInBytes, out byte scsiStatus, out int win32Error,
        byte pathId = 0, byte targetId = 0, byte lun = 0, int timeoutSeconds = 5)
    {
        dataInBytes = null; scsiStatus = 0;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (dataLength > 0)
            {
                buffer = Marshal.AllocHGlobal(dataLength);
                if (!dataIn && dataOut is not null) Marshal.Copy(dataOut, 0, buffer, Math.Min(dataLength, dataOut.Length));
                else Marshal.Copy(new byte[dataLength], 0, buffer, dataLength);
            }

            int senseLen = 32;
            int structSize = Marshal.SizeOf<SCSI_PASS_THROUGH_DIRECT>();
            var spt = new SCSI_PASS_THROUGH_DIRECT
            {
                Length = (ushort)structSize,
                ScsiStatus = 0,
                PathId = pathId, TargetId = targetId, Lun = lun,
                CdbLength = (byte)cdb.Length,
                SenseInfoLength = (byte)senseLen,
                DataIn = dataIn ? SCSI_IOCTL_DATA_IN : SCSI_IOCTL_DATA_OUT,
                DataTransferLength = (uint)dataLength,
                TimeOutValue = (uint)timeoutSeconds,
                DataBuffer = buffer,
                SenseInfoOffset = (uint)structSize,
                Cdb = new byte[16],
            };
            Array.Copy(cdb, spt.Cdb, Math.Min(16, cdb.Length));
            if (dataLength == 0) spt.DataIn = SCSI_IOCTL_DATA_UNSPECIFIED;

            byte[] inBytes = DeviceIo.StructToBytes(spt);
            var outBytes = new byte[structSize + senseLen + 64];
            bool ok = DeviceIo.Ioctl(h, IOCTL_SCSI_PASS_THROUGH_DIRECT, inBytes, inBytes.Length, outBytes, out _, out win32Error);

            var ret = DeviceIo.BytesToStruct<SCSI_PASS_THROUGH_DIRECT>(outBytes);
            scsiStatus = ret.ScsiStatus;
            if (dataLength > 0 && buffer != IntPtr.Zero)
            {
                dataInBytes = new byte[dataLength];
                Marshal.Copy(buffer, dataInBytes, 0, dataLength);
            }
            return ok && scsiStatus == 0;
        }
        catch (Exception)
        {
            win32Error = -1;
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>SMART READ DATA 的任务文件寄存器。</summary>
    public static IDEREGS SmartTask(byte subCommand, byte lbaLow = SMART_LBA_LOW, byte lbaMid = SMART_LBA_MID,
        byte sectorCount = 1, byte driveHead = 0xA0)
        => new()
        {
            Features = subCommand,
            SectorCount = sectorCount,
            LbaLow = lbaLow,
            LbaMid = lbaMid,
            LbaHigh = 0,
            DriveHead = driveHead,
            Command = ATA_SMART_CMD,
            Reserved = 0,
        };

    public static IDEREGS IdentifyTask(byte driveHead = 0xA0)
        => new() { Features = 0, SectorCount = 0, LbaLow = 0, LbaMid = 0, LbaHigh = 0,
                   DriveHead = driveHead, Command = ATA_IDENTIFY_DEVICE, Reserved = 0 };
}
