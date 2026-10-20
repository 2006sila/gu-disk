@echo off
chcp 65001 >nul
setlocal
set "EXE=%~dp0src\GuDisk.App\bin\Release\net9.0-windows\Gu-disk.exe"
if not exist "%EXE%" (
  echo.
  echo   [!] Gu-disk.exe not found.
  echo   Build it first:  powershell -ExecutionPolicy Bypass -File tools\verify.ps1
  echo.
  pause
  exit /b 1
)
rem Launch elevated so USB enclosures can pass SMART through the bridge.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%EXE%' -Verb RunAs"
exit /b 0
