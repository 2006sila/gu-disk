#Requires -Version 5.1
<#
  Gu-disk 验证脚本：构建 -> 单元测试 -> CLI 环境自检 -> 产出 verify-report.md
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
# 中文输出：PS 5.1 控制台默认走 GBK，捕获时会变乱码，这里强制 UTF-8
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$root = Split-Path -Parent $PSScriptRoot
$report = Join-Path $root 'verify-report.md'
$lines = New-Object System.Collections.Generic.List[string]
$fail = 0

function Step([string]$name, [scriptblock]$body) {
    Write-Host "==> $name" -ForegroundColor Cyan
    $out = & $body 2>&1 | Out-String
    Write-Host $out
    $script:lines.Add("## $name")
    $script:lines.Add('')
    $script:lines.Add('~~~')
    $script:lines.Add($out.Trim())
    $script:lines.Add('~~~')
    $script:lines.Add('')
    if ($LASTEXITCODE -ne 0) { $script:fail++; $script:lines.Add("> 结果：**失败** (exit $LASTEXITCODE)"); $script:lines.Add('') }
    else { $script:lines.Add('> 结果：通过'); $script:lines.Add('') }
}

$lines.Add('# Gu-disk verify 报告')
$lines.Add('')
$lines.Add("- 生成时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$lines.Add("- 配置：$Configuration")
$lines.Add('')

$testsExe = Join-Path $root ("tests\GuDisk.Tests\bin\" + $Configuration + "\net9.0-windows\Gu-disk.Tests.exe")
$cliExe = Join-Path $root ("src\GuDisk.Cli\bin\" + $Configuration + "\net9.0-windows\Gu-disk.Cli.exe")

Step '构建' { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration }
Step '单元测试' { & $testsExe }
Step 'CLI 环境自检' { & $cliExe policy --selftest }

$lines.Add('---')
$lines.Add('')
$lines.Add($(if ($fail -eq 0) { '**全部通过**' } else { "**$fail 步失败**" }))

Set-Content -Path $report -Value $lines -Encoding UTF8
Write-Host "报告已写入 $report" -ForegroundColor Green
if ($fail -ne 0) { exit 1 }
