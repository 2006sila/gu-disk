using System.Runtime.InteropServices;

namespace GuDisk.Core.Interop;

/// <summary>winioctl.h 里的存储相关结构体（仅列出本项目用到的字段）。</summary>
internal static class StorageStructures
{
    public enum StoragePropertyId
    {
        StorageDeviceProperty = 0,
        StorageAdapterProperty = 1,
        StorageAccessAlignmentProperty = 6,
        StorageDeviceSeekPenaltyProperty = 7,
        StorageDeviceTrimProperty = 8,
        StorageAdapterProtocolSpecificProperty = 49,
        StorageDeviceProtocolSpecificProperty = 50,
        StorageDeviceTemperatureProperty = 52,
    }

    public enum StorageQueryType { PropertyStandardQuery = 0, PropertyExistsQuery = 1 }

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_DEVICE_NUMBER
    {
        public uint DeviceType;
        public uint DeviceNumber;
        public uint PartitionNumber;
    }

    /// <summary>BusType 枚举（winioctl.h STORAGE_BUS_TYPE）。</summary>
    public enum StorageBusType
    {
        Unknown = 0, Scsi = 1, Atapi = 2, Ata = 3, Ieee1394 = 4, Ssa = 5, Fibre = 6,
        Usb = 7, Raid = 8, IScsi = 9, Sas = 10, Sata = 11, Sd = 12, Mmc = 13,
        Virtual = 14, FileBackedVirtual = 15, Spaces = 16, Nvme = 17, Scm = 18, Ufs = 19
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_DEVICE_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public byte DeviceType;
        public byte DeviceTypeModifier;
        public byte RemovableMedia;
        public byte CommandQueueing;
        public uint VendorIdOffset;
        public uint ProductIdOffset;
        public uint ProductRevisionOffset;
        public uint SerialNumberOffset;
        public int BusType;
        public uint RawPropertiesLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_ADAPTER_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public uint MaximumTransferLength;
        public uint MaximumPhysicalPages;
        public uint AlignmentMask;
        public byte AdapterUsesPio;
        public byte AdapterScansDown;
        public byte CommandQueueing;
        public byte AcceleratedTransfer;
        public byte BusType;
        public ushort BusMajorVersion;
        public ushort BusMinorVersion;
        public byte SrbType;
        public byte AddressType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_PROTOCOL_SPECIFIC_DATA
    {
        public int ProtocolType;                  // 1=SCSI 2=ATA 3=NVMe
        public int DataType;
        public int ProtocolDataRequestValue;
        public int ProtocolDataRequestSubValue;
        public int ProtocolDataOffset;
        public int ProtocolDataLength;
        public int FixedProtocolReturnData;
        public int ProtocolDataRequestSubValue2;
        public int ProtocolDataRequestSubValue3;
        public int ProtocolDataRequestSubValue4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_PROTOCOL_DATA_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public STORAGE_PROTOCOL_SPECIFIC_DATA ProtocolSpecificData;
    }

    public enum ProtocolType { Unknown = 0, Scsi = 1, Ata = 2, Nvme = 3 }

    /// <summary>NVME_STORAGE_PROTOCOL_DATA_TYPE（仅列常用）。</summary>
    public enum NvmeDataType
    {
        Unknown = 0,
        Identify = 1,
        LogPage = 2,
        Feature = 3,
        DeviceCapabilities = 0x11,
        ControllerRegisters = 0x12,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISK_GEOMETRY
    {
        public long Cylinders;
        public int MediaType;
        public uint TracksPerCylinder;
        public uint SectorsPerTrack;
        public uint BytesPerSector;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISK_GEOMETRY_EX
    {
        public DISK_GEOMETRY Geometry;
        public long DiskSize;
        public byte Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DRIVE_LAYOUT_INFORMATION_EX
    {
        public uint PartitionStyle;
        public uint PartitionCount;
        public IntPtr Union;   // 实际数据在后续内存里，按偏移手工解析
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PARTITION_INFORMATION_EX_HEAD
    {
        public uint PartitionStyle;
        public long StartingOffset;
        public long PartitionLength;
        public uint PartitionNumber;
        public byte RewritePartition;
        public byte IsServicePartition;
    }
}
