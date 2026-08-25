param(
    [Parameter(Mandatory = $true)] [string]$ApplicationPath,
    [Parameter(Mandatory = $true)] [string]$LibraryPath,
    [string]$OutputDirectory = "$env:TEMP\InnerTuneMiniCaptures"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class InnerTuneHiddenCapture
{
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    public static IntPtr FindWindow(int wantedProcessId)
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

$testRoot = Join-Path $env:TEMP ("InnerTuneMiniTest-" + [Guid]::NewGuid().ToString('N'))
New-Item $testRoot -ItemType Directory -Force | Out-Null
New-Item $OutputDirectory -ItemType Directory -Force | Out-Null
$testLibrary = Join-Path $testRoot 'library.json'
Copy-Item $LibraryPath $testLibrary
$seed = Get-Content $testLibrary -Raw | ConvertFrom-Json
$seed.playback.status = 'paused'
$seed.volume = 0
$seed | ConvertTo-Json -Depth 20 | Set-Content $testLibrary -Encoding utf8
$previousTestMode = $env:INNERTUNE_TEST_MODE
$previousTestInstance = $env:INNERTUNE_TEST_INSTANCE
$previousDataDirectory = $env:ITMUSIC_DATA_DIR
$previousCaptureDirectory = $env:INNERTUNE_TEST_CAPTURE_DIR
$process = $null

try {
    $env:INNERTUNE_TEST_MODE = '1'
    $env:INNERTUNE_TEST_INSTANCE = [Guid]::NewGuid().ToString('N')
    $env:ITMUSIC_DATA_DIR = $testRoot
    $env:INNERTUNE_TEST_CAPTURE_DIR = $OutputDirectory
    $process = Start-Process $ApplicationPath -PassThru
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $handle = [InnerTuneHiddenCapture]::FindWindow($process.Id)
        if ($handle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($handle -eq [IntPtr]::Zero) { throw 'InnerTune did not expose its hidden test window.' }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $captures = @(Get-ChildItem $OutputDirectory -Filter 'mini-*.png' -ErrorAction SilentlyContinue)
        if ($captures.Count -eq 4) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($captures.Count -ne 4) {
        $events = if (Test-Path (Join-Path $testRoot 'test.log')) { (Get-Content (Join-Path $testRoot 'test.log') -Tail 10) -join ' | ' } else { 'No test log.' }
        throw "InnerTune did not produce all four mini-player captures. $events"
    }
    $captures | Select-Object FullName, Length
}
finally {
    if ($process -and !$process.HasExited) {
        try { $root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
        if (!$process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force }
    }
    $env:INNERTUNE_TEST_MODE = $previousTestMode
    $env:INNERTUNE_TEST_INSTANCE = $previousTestInstance
    $env:ITMUSIC_DATA_DIR = $previousDataDirectory
    $env:INNERTUNE_TEST_CAPTURE_DIR = $previousCaptureDirectory
    if (Test-Path $testRoot) { Remove-Item $testRoot -Recurse -Force }
}
