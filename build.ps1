#Requires -Version 5.1
<#
.SYNOPSIS
  Builds the tray panel and the setup package with csc.exe from the .NET Framework that ships with Windows.
  No .NET SDK or Visual Studio is needed.

  Output:
    bin\NetWatchdogUI.exe                   tray panel
    bin\NetWatchdog-Setup-<version>.exe     setup package with the watchdog script and tray panel embedded
  The version number comes from the VERSION file.
#>
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$ui = Join-Path $root 'ui'
$setup = Join-Path $root 'setup'
$bin = Join-Path $root 'bin'
$obj = Join-Path $bin 'obj'
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw "$csc not found; .NET Framework 4.x is required" }
New-Item -ItemType Directory -Path $bin -Force | Out-Null

# App icon: white signal arcs on a blue rounded square, in 16/32/48/256 sizes (PNG-compressed ICO)
function New-AppIcon([string]$Path) {
    Add-Type -AssemblyName System.Drawing
    $images = @()
    foreach ($size in 16, 32, 48, 256) {
        $bmp = New-Object System.Drawing.Bitmap($size, $size)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.Clear([System.Drawing.Color]::Transparent)

        $s = [float]$size
        $r = $s * 0.22
        $shape = New-Object System.Drawing.Drawing2D.GraphicsPath
        $shape.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
        $shape.AddArc($s - $r * 2 - 1, 0, $r * 2, $r * 2, 270, 90)
        $shape.AddArc($s - $r * 2 - 1, $s - $r * 2 - 1, $r * 2, $r * 2, 0, 90)
        $shape.AddArc(0, $s - $r * 2 - 1, $r * 2, $r * 2, 90, 90)
        $shape.CloseFigure()
        $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF($s, $s)),
            [System.Drawing.Color]::FromArgb(255, 76, 194, 255), [System.Drawing.Color]::FromArgb(255, 0, 95, 184))
        $g.FillPath($grad, $shape)

        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [float]($s * 0.085))
        $pen.StartCap = 'Round'
        $pen.EndCap = 'Round'
        $cx = $s / 2
        $cy = $s * 0.70
        foreach ($k in 0.17, 0.31, 0.45) {
            $rr = $s * $k
            $g.DrawArc($pen, [float]($cx - $rr), [float]($cy - $rr), [float]($rr * 2), [float]($rr * 2), 225, 90)
        }
        $dot = $s * 0.065
        $g.FillEllipse([System.Drawing.Brushes]::White, [float]($cx - $dot), [float]($cy - $dot), [float]($dot * 2), [float]($dot * 2))
        $g.Dispose()

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $images += , @{ Size = $size; Bytes = $ms.ToArray() }
    }

    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($out)
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$img.Bytes.Length); $w.Write([uint32]$offset)
        $offset += $img.Bytes.Length
    }
    foreach ($img in $images) { $w.Write($img.Bytes) }
    $w.Flush()
    [IO.File]::WriteAllBytes($Path, $out.ToArray())
}

$icon = Join-Path $ui 'app.ico'
if (-not (Test-Path -LiteralPath $icon)) { New-AppIcon $icon }

# Version: x.y.z from the VERSION file, turned into assembly versions and the AppInfo.Version constant
$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION must look like x.y.z, found $version" }
New-Item -ItemType Directory -Path $obj -Force | Out-Null
# Repository: owner/name from the REPOSITORY file; the tray panel checks its GitHub Releases for updates
$repo = (Get-Content -LiteralPath (Join-Path $root 'REPOSITORY') -Raw).Trim()
if ($repo -notmatch '^[\w.-]+/[\w.-]+$') { throw "REPOSITORY must look like owner/name, found $repo" }
$versionFile = Join-Path $obj 'Version.g.cs'
$versionCode = @"
[assembly: System.Reflection.AssemblyVersion("$version.0")]
[assembly: System.Reflection.AssemblyFileVersion("$version.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("$version")]
namespace NetWatchdogUI { static class AppInfo { public const string Version = "$version"; public const string Repo = "$repo"; } }
"@
[IO.File]::WriteAllText($versionFile, $versionCode, (New-Object Text.UTF8Encoding($false)))

$wpf = Join-Path $fw 'WPF'
function Invoke-Csc([string]$Out, [string[]]$Resources, [string[]]$Sources) {
    $cscArgs = @(
        '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001', '/warn:4',
        "/out:$Out",
        "/win32icon:$icon",
        "/win32manifest:$(Join-Path $ui 'app.manifest')",
        "/lib:$wpf",
        '/r:PresentationFramework.dll', '/r:PresentationCore.dll', '/r:WindowsBase.dll', '/r:System.Xaml.dll',
        '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
        '/r:System.Web.Extensions.dll'
    )
    $cscArgs += $Resources | ForEach-Object { "/resource:$_" }
    $cscArgs += $Sources
    & $csc @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "Building $Out failed (csc exit code $LASTEXITCODE)" }
    Write-Host "Built $Out"
}

$shared = Join-Path $ui 'Shared.cs'
$styles = "$(Join-Path $ui 'Styles.xaml'),NetWatchdogUI.Styles.xaml"

# Tray panel
$panel = Join-Path $bin 'NetWatchdogUI.exe'
Invoke-Csc $panel `
    @($styles, "$(Join-Path $ui 'Flyout.xaml'),NetWatchdogUI.Flyout.xaml") `
    @($shared, (Join-Path $ui 'NetWatchdogUI.cs'), (Join-Path $ui 'Updater.cs'), $versionFile)

# Setup package: embeds the watchdog script and the freshly built tray panel
Get-ChildItem -LiteralPath $bin -Filter 'NetWatchdog-Setup-*.exe' | Remove-Item -Force
$setupExe = Join-Path $bin "NetWatchdog-Setup-$version.exe"
Invoke-Csc $setupExe `
    @($styles,
      "$(Join-Path $setup 'Setup.xaml'),NetWatchdogUI.Setup.xaml",
      "$icon,NetWatchdogUI.app.ico",
      "$panel,payload.NetWatchdogUI.exe",
      "$(Join-Path $root 'NetWatchdog.ps1'),payload.NetWatchdog.ps1") `
    @($shared, (Join-Path $setup 'Setup.cs'), $versionFile)
