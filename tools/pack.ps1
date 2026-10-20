#Requires -Version 5.1
<#
  Gu-disk 打包：单文件发布 -> 组装 data/ 与 tools/smartctl/ -> zip -> SHA256SUMS
  dist/ 只增不删。
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    # 打框架依赖版（约 1 MB，用户需预装 .NET 9 桌面运行时）
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
# 中文输出：PS 5.1 控制台默认走 GBK，捕获时会变乱码，这里强制 UTF-8
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$root = Split-Path -Parent $PSScriptRoot
$suffix = if ($FrameworkDependent) { '-framework' } else { '' }
$publishDir = if ($FrameworkDependent) { 'dist\publish-fd' } else { 'dist\publish' }
$stage = Join-Path $root ("dist\Gu-disk-" + $Version + "-win-x64" + $suffix)
$zip = $stage + '.zip'

& (Join-Path $PSScriptRoot 'build.ps1') -Publish -FrameworkDependent:$FrameworkDependent

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item (Join-Path $root ($publishDir + '\*')) $stage -Recurse -Force

# 随包引擎
$smart = Join-Path $root 'tools\smartctl'
if (Test-Path $smart) {
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'tools\smartctl') | Out-Null
    Copy-Item (Join-Path $smart '*') (Join-Path $stage 'tools\smartctl') -Force
}

# 随包数据（特征库 / 语言包 / 报告模板）
$data = Join-Path $root 'data'
if (Test-Path $data) { Copy-Item $data (Join-Path $stage 'data') -Recurse -Force }

# 清掉 .NET 自带的卫星语言目录（cs/de/es/... 各一个文件夹），根目录清爽些
Get-ChildItem $stage -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(cs|de|es|fr|it|ja|ko|pl|pt-BR|pt-PT|ru|tr|zh-Hant|zh-TW)$' } | Remove-Item -Recurse -Force

# 删掉运行时用不到的大文件（自包含发布里 .NET 会带上设计时与调试组件）
#  System.Windows.Forms.Design.dll        设计器专用，运行时不需要（5.8 MB）
#  Microsoft.DiaSymReader.Native.amd64.dll 调试符号读取，运行时不加载（2.1 MB）
# 已实测：删掉后启动正常、托盘无崩溃。合计省约 7.9 MB。
foreach ($f in @(
    'System.Windows.Forms.Design.dll',
    'Microsoft.DiaSymReader.Native.amd64.dll'
)) {
    $fp = Join-Path $stage $f
    if (Test-Path $fp) { Remove-Item $fp -Force; Write-Host "  已删除 $f" }
}

# 使用说明放根目录，用户解压第一眼就能看到
Copy-Item (Join-Path $root 'tools\README-使用说明.txt') (Join-Path $stage '使用说明.txt') -Force

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
$sumFile = Join-Path $root 'dist\SHA256SUMS.txt'
Add-Content -Path $sumFile -Value ("$hash  " + (Split-Path $zip -Leaf))

$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
$files = (Get-ChildItem $stage -Recurse -File).Count
Write-Host ""
Write-Host "已打包 $zip" -ForegroundColor Green
Write-Host "  体积 $mb MB · 文件数 $files"
Write-Host "  SHA256 $hash"
Write-Host "  校验和 $sumFile"
