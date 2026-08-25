param(
    [string]$ApplicationPath = "$env:LOCALAPPDATA\Programs\InnerTune\InnerTune.exe",
    [string]$LibraryPath = "$env:LOCALAPPDATA\InnerTune\library.json",
    [int]$SurfaceTimeoutSeconds = 240,
    [switch]$KeepTestData
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class HiddenTestWindow
{
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);

    public static IntPtr Find(int wantedProcessId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, parameter) =>
        {
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (processId != wantedProcessId || !IsWindowVisible(window)) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@

function Wait-Until([scriptblock]$Condition, [int]$Seconds = 60, [string]$Failure = 'Timed out.') {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $value = & $Condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function Wait-ForWindow([System.Diagnostics.Process]$Process) {
    return Wait-Until {
        $Process.Refresh()
        $handle = $Process.MainWindowHandle
        if ($handle -eq 0) { $handle = [HiddenTestWindow]::Find($Process.Id) }
        if ($handle -ne 0) {
            [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        }
    } 15 'InnerTune did not expose its main window.'
}

function Get-VisibleElements(
    [System.Windows.Automation.AutomationElement]$Root,
    [System.Windows.Automation.ControlType]$Type
) {
    return @($Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition) | Where-Object {
            $_.Current.ControlType -eq $Type -and
            !$_.Current.BoundingRectangle.IsEmpty
        })
}

function Get-VisibleButton(
    [System.Windows.Automation.AutomationElement]$Root,
    [string]$HelpText,
    [string]$Name
) {
    return Get-VisibleElements $Root ([System.Windows.Automation.ControlType]::Button) |
        Where-Object {
            (!$HelpText -or $_.Current.HelpText -eq $HelpText) -and
            (!$Name -or $_.Current.Name -eq $Name)
        } | Select-Object -First 1
}

function Invoke-Button([System.Windows.Automation.AutomationElement]$Button) {
    $Button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Get-BottomControls(
    [System.Windows.Automation.AutomationElement]$Root,
    [System.Windows.Automation.ControlType]$Type
) {
    $bounds = $Root.Current.BoundingRectangle
    return Get-VisibleElements $Root $Type | Where-Object {
        $_.Current.BoundingRectangle.Top -ge ($bounds.Bottom - 100)
    }
}

function Stop-InnerTune([System.Diagnostics.Process]$Process) {
    if (!$Process -or $Process.HasExited) { return }
    $Process.CloseMainWindow() | Out-Null
    if (!$Process.WaitForExit(5000)) {
        $Process.Kill()
        $Process.WaitForExit()
    }
}

$normalProcess = Get-Process InnerTune -ErrorAction SilentlyContinue | Select-Object -First 1
$normalProcessId = if ($normalProcess) { $normalProcess.Id } else { $null }

$testData = Join-Path $env:TEMP ("InnerTuneSmartVideoTest-" + [Guid]::NewGuid().ToString('N'))
New-Item $testData -ItemType Directory -Force | Out-Null
$testLibrary = Join-Path $testData 'library.json'
Copy-Item $LibraryPath $testLibrary

# Keep the test silent and deterministic while preserving the user's real state.
$seed = Get-Content $testLibrary -Raw | ConvertFrom-Json
$seed.playback.status = 'paused'
$seed.playback.positionSeconds = 0
$seed.volume = 0
$seed | ConvertTo-Json -Depth 20 | Set-Content $testLibrary -Encoding utf8
$trackId = $seed.playback.trackId
$trackTitle = $seed.playback.track.title
$testProcess = $null
$result = $null

try {
    $env:ITMUSIC_DATA_DIR = $testData
    $env:INNERTUNE_TEST_MODE = '1'
    $env:INNERTUNE_TEST_INSTANCE = [Guid]::NewGuid().ToString('N')
    $testProcess = Start-Process $ApplicationPath -PassThru
    $root = Wait-ForWindow $testProcess
    Start-Sleep -Seconds 2

    $watch = Wait-Until {
        Get-VisibleButton $root 'Watch video' $null
    } 10 'Could not find the Watch video control.'
    Invoke-Button $watch

    # High-confidence matches open immediately. Ambiguous matches intentionally
    # stop at the chooser so the user remains in control.
    try {
        $surface = Wait-Until {
            $change = Get-VisibleButton $root 'Choose another video' $null
            if ($change) { return [pscustomobject]@{ Kind = 'video'; Element = $change } }
            $use = Get-VisibleButton $root $null 'Use'
            if ($use) { return [pscustomobject]@{ Kind = 'chooser'; Element = $use } }
        } $SurfaceTimeoutSeconds 'Neither a playable video nor the video chooser appeared.'
    }
    catch {
        $logPath = Join-Path $testData 'test.log'
        $events = if (Test-Path $logPath) { (Get-Content $logPath -Tail 20) -join ' | ' } else { 'No test events were recorded.' }
        throw "$($_.Exception.Message) Events: $events"
    }

    $chooserWasNeeded = $surface.Kind -eq 'chooser'
    if ($chooserWasNeeded) {
        Invoke-Button $surface.Element
    }

    $change = Wait-Until {
        Get-VisibleButton $root 'Choose another video' $null
    } 60 'The selected video did not open.'

    $mapping = Wait-Until {
        $data = Get-Content $testLibrary -Raw | ConvertFrom-Json
        $property = $data.videoMappings.PSObject.Properties[$trackId]
        if ($property -and $property.Value.videoId) { $property.Value }
    } 20 'The selected video was not remembered in the library.'

    Invoke-Button $change
    $useButtons = Wait-Until {
        $buttons = @(Get-VisibleElements $root ([System.Windows.Automation.ControlType]::Button) |
            Where-Object { $_.Current.Name -eq 'Use' })
        if ($buttons.Count -gt 0) { $buttons }
    } 60 'The Change video chooser did not return any candidates.'

    Invoke-Button $useButtons[0]
    Wait-Until {
        Get-VisibleButton $root 'Choose another video' $null
    } 60 'The replacement video did not open.' | Out-Null

    $seek = Get-BottomControls $root ([System.Windows.Automation.ControlType]::Slider) |
        Sort-Object { $_.Current.BoundingRectangle.Width } -Descending |
        Select-Object -First 1
    if (!$seek) { throw 'Could not find the seek control.' }
    $range = $seek.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    $targetRatio = 0.6
    if ($range.Current.IsReadOnly) { throw 'The seek control is read-only to UI Automation.' }
    $range.SetValue($range.Current.Maximum * $targetRatio)
    Start-Sleep -Seconds 2

    $target = $range.Current.Maximum * $targetRatio
    $result = [pscustomobject]@{
        Track = $trackTitle
        ChooserWasNeeded = $chooserWasNeeded
        CandidateCount = @($useButtons).Count
        RememberedVideoId = $mapping.videoId
        RememberedKind = $mapping.kind
        UsesVideoAudio = [bool]$mapping.useVideoAudio
        ReplacedAudioOnlyId = $mapping.videoId -ne $trackId
        SeekAccepted = [Math]::Abs($range.Current.Value - $target) -lt [Math]::Max(2, $range.Current.Maximum * 0.04)
        ProcessResponding = $testProcess.Responding
        MainAppUnaffected = !$normalProcessId -or !!(Get-Process -Id $normalProcessId -ErrorAction SilentlyContinue)
        TestWindowHidden = !$testProcess.MainWindowHandle -or $root.Current.BoundingRectangle.Left -lt -30000
    }
}
finally {
    Stop-InnerTune $testProcess
    Remove-Item Env:ITMUSIC_DATA_DIR -ErrorAction SilentlyContinue
    Remove-Item Env:INNERTUNE_TEST_MODE -ErrorAction SilentlyContinue
    Remove-Item Env:INNERTUNE_TEST_INSTANCE -ErrorAction SilentlyContinue
    if (!$KeepTestData) { Remove-Item $testData -Recurse -Force -ErrorAction SilentlyContinue }
}

$result | ConvertTo-Json -Compress
