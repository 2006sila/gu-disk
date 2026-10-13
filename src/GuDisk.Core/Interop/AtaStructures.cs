using System.Runtime.InteropServices;

namespace GuDisk.Core.Interop;

/// <summary>ATA / SCSI 透传用的结构体（winioctl.h / ntiddisk.h）。</summary>
internal static class AtaStructures
{
    // ---- ATA_PASS_THROUGH_DIRECT.AtaFlags ----
    public const ushort ATA_FLAGS_DRDY_REQUIRED = 0x01;
    public const ushort ATA_FLAGS_DATA_IN = 0x02;
    public const ushort ATA_FLAGS_DATA_OUT = 0x04;
    public const ushort ATA_FLAGS_48BIT_COMMAND = 0x08;
    public const ushort ATA_FLAGS_USE_DMA = 0x10;
    public const ushort ATA_FLAGS_NO_MULTIPLE = 0x20;

    // ---- ATA 命令与 SMART 子命令 ----
    public const byte ATA_IDENTIFY_DEVICE = 0xEC;
    public const byte ATA_SMART_CMD = 0xB0;
    public const byte ATA_SMART_READ_DATA = 0xD0;
    public const byte ATA_SMART_READ_THRESHOLD = 0xD1;
    public const byte ATA_SMART_ENABLE_OPS = 0xD8;
    public const byte ATA_SMART_RETURN_STATUS = 0xDA;
    public const byte ATA_SMART_EXECUTE_OFFLINE = 0xD4;
    public const byte ATA_SMART_READ_LOG = 0xD5;
    public const byte ATA_SMART_WRITE_LOG = 0xD6;
    public const byte ATA_SMART_DISABLE_OPS = 0xD9;

    public const byte SMART_LBA_LOW = 0x4F;
    public const byte SMART_LBA_MID = 0xC2;

    /// <summary>SMART EXECUTE OFF-LINE IMMEDIATE 的子命令（放在 LbaLow）。</summary>
    public const byte SMART_OFFLINE_ROUTINE_NOW = 0x00;
    public const byte SMART_SHORT_SELFTEST = 0x01;
    public const byte SMART_EXTENDED_SELFTEST = 0x02;
    public const byte SMART_ABORT_SELFTEST = 0x7F;

    public const int SMART_DATA_LENGTH = 512;

    /// <summary>IDEREGS：ATA_PASS_THROUGH 的任务文件寄存器布局（8 字节）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IDEREGS
    {
        public byte Features;
        public byte SectorCount;
        public byte LbaLow;      // Sector Number
        public byte LbaMid;      // Cylinder Low
        public byte LbaHigh;     // Cylinder High
        public byte DriveHead;   // Drive/Head
        public byte Command;
        public byte Reserved;
    }

    /// <summary>ATA_PASS_THROUGH_DIRECT：DataBuffer 必须是非分页、对齐的缓冲（用 AllocHGlobal）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ATA_PASS_THROUGH_DIRECT
    {
        public ushort Length;
        public ushort AtaFlags;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte ReservedAsUchar;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public IDEREGS PreviousTaskFile;
        public IDEREGS CurrentTaskFile;
    }

    // ---- SCSI_PASS_THROUGH_DIRECT ----
    [StructLayout(LayoutKind.Sequential)]
    public struct SCSI_PASS_THROUGH_DIRECT
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;          // 1 = SCSI_IOCTL_DATA_IN
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Cdb;
    }

    public const byte SCSI_IOCTL_DATA_OUT = 0;
    public const byte SCSI_IOCTL_DATA_IN = 1;
    public const byte SCSI_IOCTL_DATA_UNSPECIFIED = 2;

    // ---- SCSI CDB：ATA PASS-THROUGH (16) = 0x85 ----
    public const byte SCSIOP_ATA_PASSTHROUGH16 = 0x85;
    public const byte SCSIOP_ATA_PASSTHROUGH12 = 0xA1;

    /// <summary>ATA PASS-THROUGH(16) 的协议字段（CDB[1] 左移一位后的取值）。</summary>
    public enum AtaProtocol : byte
    {
        NonData = 3,
        PioDataIn = 4,
        PioDataOut = 5,
        Dma = 6,
        ReturnResponseInfo = 7
    }

    /// <summary>t_length（CDB[2] 低 2 位）。</summary>
    public enum AtaTransfers : byte
    {
        NoData = 0,
        Feature = 1,
        SectorCount = 2,
        TransferBlock = 3
    }

    /// <summary>
    /// 组装 ATA PASS-THROUGH(16) 的 16 字节 CDB。
    /// 布局： [0]=0x85 [1]=protocol&lt;&lt;1 [2]=t_length|t_dir|ck_cond|offline|extend
    ///        [3..4]=Features [5..6]=SectorCount [7..8]=LbaLow [9..10]=LbaMid
    ///        [11..12]=LbaHigh [13]=Device [14]=Command [15]=Control
    /// </summary>
    public static byte[] BuildAtaPassThrough16(AtaProtocol protocol, AtaTransfers transfers,
        ushort features, ushort sectorCount, ushort lbaLow, ushort lbaMid, ushort lbaHigh,
        byte device, byte command, bool checkCondition = true, bool extend = false)
    {
        var cdb = new byte[16];
        cdb[0] = SCSIOP_ATA_PASSTHROUGH16;
        cdb[1] = (byte)((byte)protocol << 1);
        cdb[2] = (byte)(((byte)transfers & 0x03)
                        | (protocol == AtaProtocol.PioDataIn || protocol == AtaProtocol.Dma ? 0x10 : 0x00)  // t_dir：1=设备→主机
                        | (checkCondition ? 0x20 : 0x00)
                        | (extend ? 0x40 : 0x00));
        cdb[3] = (byte)(features >> 8); cdb[4] = (byte)(features & 0xFF);
        cdb[5] = (byte)(sectorCount >> 8); cdb[6] = (byte)(sectorCount & 0xFF);
        cdb[7] = (byte)(lbaLow >> 8); cdb[8] = (byte)(lbaLow & 0xFF);
        cdb[9] = (byte)(lbaMid >> 8); cdb[10] = (byte)(lbaMid & 0xFF);
        cdb[11] = (byte)(lbaHigh >> 8); cdb[12] = (byte)(lbaHigh & 0xFF);
        cdb[13] = device;
        cdb[14] = command;
        cdb[15] = 0;
        return cdb;
    }

    // ---- ATA IDENTIFY DEVICE 字偏移 ----
    public static class IdentifyWord
    {
        public const int Serial = 10;        // 10-19，20 字符
        public const int Firmware = 23;      // 23-26，8 字符
        public const int Model = 27;         // 27-46，40 字符
        public const int Lba48Sectors = 100; // 100-103，48 位扇区数
        public const int PhysicalSector = 106;// bit 14=有效；11:8=逻辑扇区 2^n；3:0=物理扇区 2^n
        public const int TrimSupported = 169;// bit 0 = 支持 DATA SET MANAGEMENT (TRIM)
        public const int RotationRate = 217; // 0x0001=SSD，0x0401-0xFFFE=RPM
        public const int SataSpeed = 76;     // bit1=Gen1 bit2=Gen2 bit3=Gen3
        public const int MajorVersion = 80;
        public const int ZonedCapability = 69;// bit 14/bit 8 都与 Zoned 特性相关（以硬件实测为准）
    }
}
