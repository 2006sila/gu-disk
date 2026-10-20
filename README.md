# Gu-disk · 磁盘全检

Windows 桌面硬盘全检工具。覆盖 **NVMe SSD / SATA 固态 / 机械盘 / USB 硬盘盒**，
一次看全：设备与桥接识别 → SMART → **状态判定** → 温度与历史趋势 → 基准测速 →
坏道扫描 → 自检与错误日志 → 诊断报告 → 托盘告警。

- 技术栈：C# / .NET 9 + WPF，**零 NuGet 第三方包**
- 驱动：**不装任何内核驱动**，纯用户态 DeviceIoControl
- 引擎：**双引擎** —— 自研原生 IOCTL 引擎（默认，快）+ smartctl（兜底 + 逐字段交叉校验）
- 非目标：分区编辑 / 格式化 / 文件恢复 / 磁盘克隆 / **健康评分**

## 下载

见 [Releases](../../releases)。**两个版本，按需选一个**：

| 文件 | 体积 | 需要什么 |
|---|---|---|
| `Gu-disk-x.y.z-win-x64.zip` | **62 MB** | 什么都不用装，解压双击即用（内含 .NET 9 运行时） |
| `Gu-disk-x.y.z-win-x64-framework.zip` | **0.9 MB** | 需预装 [.NET 9 桌面运行时](https://dotnet.microsoft.com/download/dotnet/9.0) |

两个版本功能**完全相同**，只是运行时打包方式不同。
62 MB 里约 145 MB（解压后）是 .NET 运行时本身，本项目自己的代码只有约 170 KB。
内含 SHA256SUMS.txt 供校验。

> 首次使用建议以管理员身份运行（右键 → 以管理员身份运行，或双击仓库里的 `run-gudisk.cmd`）——
> USB 硬盘盒的 SMART 必须提权才能穿过桥接读取，这是硬件限制，CrystalDiskInfo 同样要求。

## 功能

| 页面 | 内容 |
|---|---|
| 硬盘概览 | 仪表式总览：状态判定 / 关键指标 / 温度历史折线（近 30 天）/ 分区分析 |
| S.M.A.R.T. | 逐条属性 + 原始值（十进制与十六进制同口径）+ 中文解读，NVMe 与 ATA 统一呈现 |
| 自检与工具 | 硬件自检（短/扩展/中止）、错误日志、AAM/APM |
| 基准测速 | CrystalDiskMark 式四档 + 自定义块大小/队列/线程/混合读写，实时速率曲线与 CPU 占用 |
| 坏道扫描 | 表面扫描，**按每块耗时分七档**着色 + 热力地图（自动降采样）；跨次对比"新增了哪些坏块" |
| 诊断报告 | HTML / TXT / JSON 导出，含温度历史 SVG |
| 设置 | 主题 / 阈值 / 告警 / 桌面温度悬浮窗（逐盘勾选）/ 硬盘盒模式 / RAID 控制器模式 |
| 托盘 | 常驻 + 告警留档 |

**状态判定不做评分**：纯谓词四档（正常/注意/警告/危急），每条结论都指回一个真实存在的
SMART 字段（来源 → 原始值 → 判读 + 建议），正常项也列出来 —— 让人看见核对了什么。
变化速率判定依据 Backblaze 的统计结论（"一天涨 20 与五年涨 60 不是一回事"）。

## 已验证的硬件

| 硬件 | 状态 |
|---|---|
| NVMe SSD（PCIe） | ✅ 全功能真机验证 |
| USB 硬盘盒 + NVMe 盘（Realtek RTL9210B + KIOXIA） | ✅ 全功能真机验证（0.2.5） |
| SATA / 机械盘 | ⏳ 代码按 ACS-3/ACS-4 规范实现 + 单测覆盖，**待真机** |
| RAID 控制器 | ⚠️ 卷识别 ✅；成员盘经 smartctl 私有协议（-d 可指定），待真机 |

> USB 硬盘盒的 SMART 读取**需要管理员权限**（桥接芯片要求打开设备才能穿桥），
> 设置页提供一键提权。这是桥接的物理限制，CrystalDiskInfo 同样要求。

## 构建

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify.ps1   # 构建 + 32 项测试 + 环境自检
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/pack.ps1 -Version x.y.z   # 自包含发布包
```

需要 .NET 9 SDK。`tools/smartctl/` 下的 smartctl.exe 与 drivedb.h 不入库，
请从 [smartmontools](https://www.smartmontools.org/) 获取（7.5 验证通过）放入该目录。

## 目录地图

```
gu-disk/
├─ docs/                       规划 / 对照清单 / 真机验收清单
├─ src/
│  ├─ GuDisk.Core/             互操作 + 全部业务逻辑（无 UI，可单测）
│  ├─ GuDisk.App/              WPF 界面（七页 + 悬浮窗 + 托盘）
│  └─ GuDisk.Cli/              命令行（14 命令，脚本化验证入口）
├─ tests/GuDisk.Tests/         32 项自检（含 SMART/错误日志字节布局回归）
├─ tools/                      构建 / 打包 / 验证脚本 + 特征库转换器
├─ data/                       特征库（CrystalDiskInfo lang + drivedb 索引）
└─ reference/                  设计稿与 UI 截图
```

## 许可

本项目代码自有。运行时依赖 [smartmontools](https://www.smartmontools.org/)（GPL-2.0+）
以**独立进程**方式调用、不链接其源码；属性名特征库衍生自
CrystalDiskInfo / smartmontools 的数据文件（转换脚本见 tools/）。
