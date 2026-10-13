param([int]$WindowId,[string]$Out,[int]$W=1280,[int]$H=840,[int]$TopOffset=28)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)] public struct RECT{public int L,T,R,B;}
[StructLayout(LayoutKind.Sequential)] public struct POINT{public int X,Y;}
public class U32{
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
[void][U32]::SetProcessDPIAware()
$h=[IntPtr]::new($WindowId)
[void][U32]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 600
$p=New-Object POINT; $p.X=0; $p.Y=0
[void][U32]::ClientToScreen($h,[ref]$p)
$r=New-Object RECT
[void][U32]::GetClientRect($h,[ref]$r)
$cw=$r.R; $ch=$r.B-$TopOffset
$bmp=New-Object System.Drawing.Bitmap $cw,$ch
$g=[System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($p.X,($p.Y+$TopOffset),0,0,(New-Object System.Drawing.Size $cw,$ch))
$crop=New-Object System.Drawing.Bitmap $W,$H
$g2=[System.Drawing.Graphics]::FromImage($crop)
$g2.DrawImage($bmp,(New-Object System.Drawing.Rectangle 0,0,$W,$H),(New-Object System.Drawing.Rectangle 0,0,$W,$H),[System.Drawing.GraphicsUnit]::Pixel)
$ms=New-Object System.IO.MemoryStream
$crop.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png)
$bytes=$ms.ToArray()
$g.Dispose();$g2.Dispose();$bmp.Dispose();$crop.Dispose();$ms.Dispose()
[System.IO.File]::WriteAllBytes($Out,$bytes)
"OK $Out " + $bytes.Length + " bytes  (client $cw x $ch at " + $p.X + "," + $p.Y + ")"