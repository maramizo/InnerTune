param(
    [string]$OutputDirectory = "$env:TEMP\InnerTuneResponsiveCaptures",
    [switch]$RestoreOnly
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class InnerTuneWindowTools
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$process = Get-Process InnerTune | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$buttons = $root.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
$rootBounds = $root.Current.BoundingRectangle
$miniButton = $buttons | Where-Object {
    $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
    !$_.Current.BoundingRectangle.IsEmpty -and
    $_.Current.BoundingRectangle.Left -ge $rootBounds.Left -and
    $_.Current.BoundingRectangle.Right -le $rootBounds.Right -and
    $_.Current.BoundingRectangle.Bottom -le $rootBounds.Bottom -and
    $_.Current.BoundingRectangle.Top -ge ($rootBounds.Bottom - 100)
} | Sort-Object @{ Expression = { $_.Current.BoundingRectangle.Left }; Descending = $true },
    @{ Expression = { $_.Current.BoundingRectangle.Top }; Descending = $true } | Select-Object -First 1
if (-not $miniButton) {
    $buttonNames = $buttons | Where-Object {
        $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button
    } | ForEach-Object { $_.Current.Name }
    throw "Could not find the mini-player button. Buttons: $($buttonNames -join ' | ')"
}

$invoke = $miniButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$bounds = $miniButton.Current.BoundingRectangle
Write-Host "Invoking mini player button '$($miniButton.Current.Name)' at $bounds"
$invoke.Invoke()
Start-Sleep -Milliseconds 500
if ($RestoreOnly) { return }

New-Item $OutputDirectory -ItemType Directory -Force | Out-Null
foreach ($width in @(720, 560, 440, 320)) {
    [InnerTuneWindowTools]::SetWindowPos($process.MainWindowHandle, [IntPtr]::Zero, 100, 100, $width, 98, 0x0014) | Out-Null
    Start-Sleep -Milliseconds 350
    $rect = New-Object InnerTuneWindowTools+RECT
    [InnerTuneWindowTools]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
    $bitmap = New-Object System.Drawing.Bitmap ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $OutputDirectory "mini-$width.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$miniBounds = $root.Current.BoundingRectangle
$expandButton = $root.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition) | Where-Object {
        $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
        !$_.Current.BoundingRectangle.IsEmpty -and
        $_.Current.BoundingRectangle.Left -ge $miniBounds.Left -and
        $_.Current.BoundingRectangle.Right -le $miniBounds.Right -and
        $_.Current.BoundingRectangle.Bottom -le $miniBounds.Bottom -and
        $_.Current.BoundingRectangle.Top -ge ($miniBounds.Bottom - 100)
    } | Sort-Object @{ Expression = { $_.Current.BoundingRectangle.Left }; Descending = $true },
        @{ Expression = { $_.Current.BoundingRectangle.Top }; Descending = $true } | Select-Object -First 1
if ($expandButton) {
    $expandButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

Get-ChildItem $OutputDirectory -Filter 'mini-*.png' | Select-Object FullName, Length
