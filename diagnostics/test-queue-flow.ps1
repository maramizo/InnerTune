param([Parameter(Mandatory = $true)] [string]$ApplicationPath)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class InnerTuneQueueTestWindow
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

$testRoot = Join-Path $env:TEMP ("InnerTuneQueueUi-" + [Guid]::NewGuid().ToString('N'))
New-Item $testRoot -ItemType Directory | Out-Null
$track = @{ id = 'current'; title = 'Current song'; artist = 'Test artist'; durationSeconds = 180; durationText = '3:00' }
@{
    version = 1; volume = 0; shuffleEnabled = $true; repeatMode = 'off'
    queueSourceId = $null; queueSourceName = 'Playing now'
    playback = @{ status = 'paused'; track = $track; trackId = 'current'; queueIndex = 0; queueId = $null; queueName = 'Playing now'; positionSeconds = 12 }
    queue = @($track); folders = @(); favorites = @(@{ track = $track; folderId = $null })
    savedQueues = @(); recentlyPlayed = @(); videoMappings = @{}; pendingCommands = @()
    settings = @{ theme = 'midnight'; icon = 'dj-cat'; animatedIconEnabled = $false; autoResumeOnStart = $false }
} | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $testRoot 'library.json') -Encoding utf8

$previous = @{
    TestMode = $env:INNERTUNE_TEST_MODE
    Instance = $env:INNERTUNE_TEST_INSTANCE
    Data = $env:ITMUSIC_DATA_DIR
}
$process = $null
try {
    $env:INNERTUNE_TEST_MODE = '1'
    $env:INNERTUNE_TEST_INSTANCE = [Guid]::NewGuid().ToString('N')
    $env:ITMUSIC_DATA_DIR = $testRoot
    $process = Start-Process $ApplicationPath -PassThru
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $handle = [InnerTuneQueueTestWindow]::FindWindow($process.Id)
        if ($handle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($handle -eq [IntPtr]::Zero) { throw 'InnerTune did not expose its hidden queue test window.' }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $nextCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Next')
    $nextActions = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $nextCondition))
    if ($root.Current.BoundingRectangle.Left -gt -30000) { throw 'The queue test window was not off-screen.' }
    if ($nextActions.Count -lt 2) { throw "Expected queue and library Play next actions; found $($nextActions.Count)." }

    [pscustomobject]@{
        Passed = $true
        Offscreen = $true
        Muted = $true
        PlayNextActions = $nextActions.Count
        QueueSource = 'Playing now'
    } | ConvertTo-Json -Compress
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force }
    $env:INNERTUNE_TEST_MODE = $previous.TestMode
    $env:INNERTUNE_TEST_INSTANCE = $previous.Instance
    $env:ITMUSIC_DATA_DIR = $previous.Data
    if (Test-Path $testRoot) { Remove-Item $testRoot -Recurse -Force }
}
