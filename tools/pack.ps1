#Requires -Version 5.1
<#
  Gu-disk 打包：单文件发布 -> 组装 data/ 与 tools/smartctl/ -> zip -> SHA256SUMS
  dist/ 只增不删。
#>
[CmdletBinding()]
param([string]$Version = '0.1.0')

$ErrorActionPreference = 'Stop'
# 中文输出：PS 5.1 控制台默认走 GBK，捕获时会变乱码，这里强制 UTF-8
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root ("dist\Gu-disk-" + $Version + "-win-x64")
$zip = $stage + '.zip'

& (Join-Path $PSScriptRoot 'build.ps1') -Publish

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item (Join-Path $root 'dist\publish\*') $stage -Recurse -Force

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
