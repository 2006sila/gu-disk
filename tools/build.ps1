#Requires -Version 5.1
<#
  Gu-disk 构建脚本（与工程约定一致）
  - 多 ProjectReference 一律 -m:1：dotnet build 对多引用会静默失败
  - 把 dotnet / nuget 缓存指到工作区：沙箱内首跑创建 %USERPROFILE%\.dotnet 会被拒
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
# 中文输出：PS 5.1 控制台默认走 GBK，捕获时会变乱码，这里强制 UTF-8
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$root = Split-Path -Parent $PSScriptRoot

$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
$env:NUGET_PACKAGES = Join-Path $root '.nuget'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null
New-Item -ItemType Directory -Force -Path $env:NUGET_PACKAGES | Out-Null

Write-Host "== Gu-disk 构建 ==  Configuration=$Configuration" -ForegroundColor Cyan
Write-Host "   DOTNET_CLI_HOME = $env:DOTNET_CLI_HOME"

$projects = @(
    'src\GuDisk.Core\GuDisk.Core.csproj',
    'src\GuDisk.App\GuDisk.App.csproj',
    'src\GuDisk.Cli\GuDisk.Cli.csproj',
    'tests\GuDisk.Tests\GuDisk.Tests.csproj'
)

foreach ($p in $projects) {
    $full = Join-Path $root $p
    Write-Host "--> $p" -ForegroundColor DarkCyan
    & dotnet msbuild $full -t:Restore,Build -p:Configuration=$Configuration -m:1 -nologo -v:m
    if ($LASTEXITCODE -ne 0) { throw "构建失败：$p (exit $LASTEXITCODE)" }
}

if ($Publish) {
    $out = Join-Path $root 'dist\publish'
    # 自包含发布：把 .NET 运行时一起打进包，用户解压双击即可运行，不需要预装任何东西。
    # 不用单文件：WPF + 自包含的单文件会在首次运行时把原生库解到 %TEMP%，
    # 既慢又受临时目录策略影响；目录形式没有这个过程，最稳。
    Write-Host "--> 自包含发布到 $out（体积较大，属预期）" -ForegroundColor DarkCyan
    & dotnet publish (Join-Path $root 'src\GuDisk.App\GuDisk.App.csproj') `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none `
        -o $out -nologo -v:m
    if ($LASTEXITCODE -ne 0) { throw "发布失败 (exit $LASTEXITCODE)" }
    Get-ChildItem $out | ForEach-Object { Write-Host ("    " + $_.Name + "  " + [math]::Round($_.Length / 1KB, 1) + " KB") }
}

Write-Host "== 构建完成 ==" -ForegroundColor Green
