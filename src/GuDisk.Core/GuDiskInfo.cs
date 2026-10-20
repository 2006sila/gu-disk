namespace GuDisk.Core;

/// <summary>产品元信息（单一来源，UI / CLI / 报告都引用这里）。</summary>
public static class GuDiskInfo
{
    public const string ProductName = "Gu-disk";
    public const string ProductSubtitle = "磁盘全检";
    public const string Version = "0.2.6";
    /// <summary>阶段标签。「关于」与 CLI 里显示成"产品名 版本（阶段）"。
    /// 路线图的 M0–M6（打包）已全部完成，不再是"M0"。</summary>
    public const string Stage = "发布版";

    /// <summary>原生引擎（自研 IOCTL）。</summary>
    public const string NativeEngine = "Native";

    /// <summary>smartctl 引擎（兜底 + 交叉校验）。</summary>
    public const string SmartctlEngine = "smartctl";
}
