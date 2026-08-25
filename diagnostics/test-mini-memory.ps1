param(
    [Parameter(Mandatory = $true)] [string]$ApplicationPath,
    [Parameter(Mandatory = $true)] [string]$LibraryPath,
    [ValidateRange(3, 10)] [int]$CycleCount = 5
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class InnerTuneMemoryWindow
{
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    public static IntPtr Find(int wantedProcessId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, parameter) => {
            uint processId; GetWindowThreadProcessId(window, out processId);
            if (processId == wantedProcessId && IsWindowVisible(window)) { found = window; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

$testRoot = Join-Path $env:TEMP ("InnerTuneMemoryTest-" + [Guid]::NewGuid().ToString('N'))
New-Item $testRoot -ItemType Directory -Force | Out-Null
Copy-Item $LibraryPath (Join-Path $testRoot 'library.json')
$seed = Get-Content (Join-Path $testRoot 'library.json') -Raw | ConvertFrom-Json
$seed.volume = 0
$seed.playback.status = 'paused'
$seed | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $testRoot 'library.json') -Encoding utf8
$oldMode = $env:INNERTUNE_TEST_MODE
$oldInstance = $env:INNERTUNE_TEST_INSTANCE
$oldData = $env:ITMUSIC_DATA_DIR
$process = $null
try {
    $env:INNERTUNE_TEST_MODE = '1'
    $env:INNERTUNE_TEST_INSTANCE = [Guid]::NewGuid().ToString('N')
    $env:ITMUSIC_DATA_DIR = $testRoot
    $process = Start-Process $ApplicationPath -PassThru
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $handle = [InnerTuneMemoryWindow]::Find($process.Id)
        if ($handle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($handle -eq [IntPtr]::Zero) { throw 'InnerTune did not expose its hidden test window.' }
    Start-Sleep -Seconds 8
    $process.Refresh()
    $before = [pscustomobject]@{ WorkingSetMB = [math]::Round($process.WorkingSet64 / 1MB, 1); PrivateMB = [math]::Round($process.PrivateMemorySize64 / 1MB, 1); Handles = $process.HandleCount }
    function Invoke-MiniToggle {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        $bounds = $root.Current.BoundingRectangle
        $button = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and !$_.Current.BoundingRectangle.IsEmpty -and $_.Current.BoundingRectangle.Top -ge ($bounds.Bottom - 110) } |
            Sort-Object @{ Expression = { $_.Current.BoundingRectangle.Left }; Descending = $true } | Select-Object -First 1
        if (!$button) { throw 'Could not find the mini-player toggle.' }
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    $miniSamples = @()
    for ($cycle = 1; $cycle -le $CycleCount; $cycle++) {
        Invoke-MiniToggle
        Start-Sleep -Seconds 3
        $process.Refresh()
        $miniSamples += [pscustomobject]@{ Cycle = $cycle; WorkingSetMB = [math]::Round($process.WorkingSet64 / 1MB, 1); PrivateMB = [math]::Round($process.PrivateMemorySize64 / 1MB, 1); Handles = $process.HandleCount }
        if ($cycle -lt $CycleCount) { Invoke-MiniToggle; Start-Sleep -Seconds 2 }
    }
    $after = $miniSamples[-1]
    [pscustomobject]@{ BeforeMini = $before; MiniCycles = $miniSamples; WorkingSetReleasedMB = [math]::Round($before.WorkingSetMB - $after.WorkingSetMB, 1); PrivateGrowthAcrossCyclesMB = [math]::Round($after.PrivateMB - $miniSamples[0].PrivateMB, 1); HandleGrowthAcrossCycles = $after.Handles - $miniSamples[0].Handles } | ConvertTo-Json -Depth 5
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force }
    $env:INNERTUNE_TEST_MODE = $oldMode
    $env:INNERTUNE_TEST_INSTANCE = $oldInstance
    $env:ITMUSIC_DATA_DIR = $oldData
    if (Test-Path $testRoot) { Remove-Item $testRoot -Recurse -Force }
}
