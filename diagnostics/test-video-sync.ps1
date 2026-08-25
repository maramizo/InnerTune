param(
    [string]$ApplicationPath = "$env:LOCALAPPDATA\Programs\InnerTune\InnerTune.exe",
    [string]$LibraryPath = "$env:LOCALAPPDATA\InnerTune\library.json"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type @'
using System.Runtime.InteropServices;
public static class VideoSyncMouse
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, System.UIntPtr extraInfo);
}
'@

function Wait-ForWindow([System.Diagnostics.Process]$Process) {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne 0) {
            return [System.Windows.Automation.AutomationElement]::FromHandle($Process.MainWindowHandle)
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'InnerTune did not expose its main window.'
}

function Get-BottomControls([System.Windows.Automation.AutomationElement]$Root, [System.Windows.Automation.ControlType]$Type) {
    $bounds = $Root.Current.BoundingRectangle
    return $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition) | Where-Object {
            $_.Current.ControlType -eq $Type -and
            !$_.Current.BoundingRectangle.IsEmpty -and
            $_.Current.BoundingRectangle.Left -ge $bounds.Left -and
            $_.Current.BoundingRectangle.Right -le $bounds.Right -and
            $_.Current.BoundingRectangle.Bottom -le $bounds.Bottom -and
            $_.Current.BoundingRectangle.Top -ge ($bounds.Bottom - 100)
        } | Sort-Object @{ Expression = { $_.Current.BoundingRectangle.Left }; Descending = $true }
}

function Get-PlayerTitle([System.Windows.Automation.AutomationElement]$Root) {
    $bounds = $Root.Current.BoundingRectangle
    return $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition) | Where-Object {
            $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and
            !$_.Current.BoundingRectangle.IsEmpty -and
            $_.Current.BoundingRectangle.Left -lt ($bounds.Left + 250) -and
            $_.Current.BoundingRectangle.Top -ge ($bounds.Bottom - 100) -and
            ![string]::IsNullOrWhiteSpace($_.Current.Name)
        } | Sort-Object { $_.Current.BoundingRectangle.Top } | Select-Object -First 1
}

$normalProcess = Get-Process InnerTune -ErrorAction SilentlyContinue | Select-Object -First 1
if ($normalProcess) {
    $normalProcess.CloseMainWindow() | Out-Null
    $normalProcess.WaitForExit(10000) | Out-Null
}

$testData = Join-Path $env:TEMP ("InnerTuneVideoSyncTest-" + [Guid]::NewGuid().ToString('N'))
New-Item $testData -ItemType Directory -Force | Out-Null
Copy-Item $LibraryPath (Join-Path $testData 'library.json')
$testProcess = $null
$result = $null
try {
    $env:ITMUSIC_DATA_DIR = $testData
    $testProcess = Start-Process $ApplicationPath -PassThru
    $root = Wait-ForWindow $testProcess
    Start-Sleep -Seconds 3

    $before = (Get-PlayerTitle $root).Current.Name
    $buttons = @(Get-BottomControls $root ([System.Windows.Automation.ControlType]::Button))
    $videoButton = $buttons | Where-Object { $_.Current.HelpText -eq 'Watch video' } | Select-Object -First 1
    if (!$videoButton) { throw 'Could not find the video control.' }
    $videoButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $videoReady = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Milliseconds 500
        $loading = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) | Where-Object {
                $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and
                $_.Current.Name -eq 'Loading video…' -and !$_.Current.IsOffscreen
            }
        $titleCopies = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) | Where-Object {
                $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and
                $_.Current.Name -eq $before -and !$_.Current.IsOffscreen
            }
        if ($loading.Count -eq 0 -and $titleCopies.Count -ge 2) { $videoReady = $true; break }
    }
    if (!$videoReady) { throw 'Video did not finish opening within 30 seconds.' }

    $buttons = @(Get-BottomControls $root ([System.Windows.Automation.ControlType]::Button))
    $nextButton = $buttons | Where-Object { $_.Current.HelpText -eq 'Next' } | Select-Object -First 1
    if (!$nextButton) { throw 'Could not find the next control.' }
    $nextButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $after = $before
    $videoTitleMatched = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 500
        $after = (Get-PlayerTitle $root).Current.Name
        if ($after -eq $before) { continue }
        $matches = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) | Where-Object {
                $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and
                $_.Current.Name -eq $after -and !$_.Current.IsOffscreen
            }
        if ($matches.Count -ge 2) { $videoTitleMatched = $true; break }
    }
    if (!$videoTitleMatched) { throw "Video did not follow the audio track change. Before='$before', after='$after'." }

    $sliders = @(Get-BottomControls $root ([System.Windows.Automation.ControlType]::Slider))
    $seek = $sliders | Sort-Object { $_.Current.BoundingRectangle.Width } -Descending | Select-Object -First 1
    $range = $seek.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    $target = $range.Current.Maximum * 0.6
    $seekBounds = $seek.Current.BoundingRectangle
    [VideoSyncMouse]::SetCursorPos(
        [int]($seekBounds.Left + ($seekBounds.Width * 0.6)),
        [int]($seekBounds.Top + ($seekBounds.Height / 2))) | Out-Null
    [VideoSyncMouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [VideoSyncMouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds 2

    $result = [pscustomobject]@{
        VideoOpened = $videoReady
        PreviousTrack = $before
        CurrentTrack = $after
        VideoFollowedTrack = $videoTitleMatched
        SeekAccepted = [Math]::Abs($range.Current.Value - $target) -lt [Math]::Max(2, $range.Current.Maximum * 0.03)
        ProcessResponding = $testProcess.Responding
    }
}
finally {
    if ($testProcess -and !$testProcess.HasExited) {
        $testProcess.CloseMainWindow() | Out-Null
        $testProcess.WaitForExit(10000) | Out-Null
    }
    Remove-Item Env:ITMUSIC_DATA_DIR -ErrorAction SilentlyContinue
    Start-Process $ApplicationPath | Out-Null
}

$result | ConvertTo-Json -Compress
