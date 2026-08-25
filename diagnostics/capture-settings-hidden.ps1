param(
    [Parameter(Mandatory = $true)] [string]$ApplicationPath,
    [string]$OutputDirectory = "$env:TEMP\InnerTuneSettingsCapture",
    [ValidateSet('settings', 'saved')] [string]$View = 'settings',
    [string]$LibraryPath,
    [ValidateRange(0, 144)] [int]$IconFrame = 0,
    [ValidateRange(0, 1)] [double]$EqualizerLevel = 0.7,
    [switch]$ExpandActiveQueue
)

$ErrorActionPreference = 'Stop'
$testRoot = Join-Path $env:TEMP ("InnerTuneSettingsTest-" + [Guid]::NewGuid().ToString('N'))
New-Item $testRoot -ItemType Directory -Force | Out-Null
New-Item $OutputDirectory -ItemType Directory -Force | Out-Null
if ($LibraryPath) {
    $testLibrary = Join-Path $testRoot 'library.json'
    Copy-Item $LibraryPath $testLibrary
    $seed = Get-Content $testLibrary -Raw | ConvertFrom-Json
    $seed.volume = 0
    $seed.playback.status = 'paused'
    $seed | ConvertTo-Json -Depth 30 | Set-Content $testLibrary -Encoding utf8
}
$previous = @{
    TestMode = $env:INNERTUNE_TEST_MODE
    Instance = $env:INNERTUNE_TEST_INSTANCE
    Data = $env:ITMUSIC_DATA_DIR
    Capture = $env:INNERTUNE_TEST_CAPTURE_DIR
    View = $env:INNERTUNE_TEST_CAPTURE_VIEW
    IconFrame = $env:INNERTUNE_TEST_ICON_FRAME
    EqualizerLevel = $env:INNERTUNE_TEST_EQUALIZER_LEVEL
    ExpandActiveQueue = $env:INNERTUNE_TEST_EXPAND_ACTIVE_QUEUE
}
$process = $null
try {
    $env:INNERTUNE_TEST_MODE = '1'
    $env:INNERTUNE_TEST_INSTANCE = [Guid]::NewGuid().ToString('N')
    $env:ITMUSIC_DATA_DIR = $testRoot
    $env:INNERTUNE_TEST_CAPTURE_DIR = $OutputDirectory
    $env:INNERTUNE_TEST_CAPTURE_VIEW = $View
    $env:INNERTUNE_TEST_ICON_FRAME = $IconFrame
    $env:INNERTUNE_TEST_EQUALIZER_LEVEL = $EqualizerLevel.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:INNERTUNE_TEST_EXPAND_ACTIVE_QUEUE = if ($ExpandActiveQueue) { '1' } else { '0' }
    $process = Start-Process $ApplicationPath -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $captureName = if ($View -eq 'saved') { 'saved-queues.png' } else { 'settings.png' }
    do {
        $capture = Get-Item (Join-Path $OutputDirectory $captureName) -ErrorAction SilentlyContinue
        if ($capture) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (!$capture) {
        $events = if (Test-Path (Join-Path $testRoot 'test.log')) { (Get-Content (Join-Path $testRoot 'test.log') -Tail 10) -join ' | ' } else { 'No test log.' }
        throw "InnerTune did not produce the $View capture. $events"
    }
    $saved = Get-Content (Join-Path $testRoot 'library.json') -Raw | ConvertFrom-Json
    [pscustomobject]@{
        Capture = $capture.FullName
        Length = $capture.Length
        DefaultTheme = $saved.settings.theme
        DefaultIcon = $saved.settings.icon
        AutoResume = $saved.settings.autoResumeOnStart
    }
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force }
    $env:INNERTUNE_TEST_MODE = $previous.TestMode
    $env:INNERTUNE_TEST_INSTANCE = $previous.Instance
    $env:ITMUSIC_DATA_DIR = $previous.Data
    $env:INNERTUNE_TEST_CAPTURE_DIR = $previous.Capture
    $env:INNERTUNE_TEST_CAPTURE_VIEW = $previous.View
    $env:INNERTUNE_TEST_ICON_FRAME = $previous.IconFrame
    $env:INNERTUNE_TEST_EQUALIZER_LEVEL = $previous.EqualizerLevel
    $env:INNERTUNE_TEST_EXPAND_ACTIVE_QUEUE = $previous.ExpandActiveQueue
    if (Test-Path $testRoot) { Remove-Item $testRoot -Recurse -Force }
}
