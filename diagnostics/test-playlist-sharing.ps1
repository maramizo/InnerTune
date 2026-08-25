param(
    [Parameter(Mandatory = $true)]
    [string]$ApplicationPath,
    [Parameter(Mandatory = $true)]
    [string]$LibraryPath,
    [switch]$KeepTestData
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient

function Wait-ForWindow([System.Diagnostics.Process]$Process, [string]$Title = '') {
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $Process.Id)
        $scope = if ($Title) {
            [System.Windows.Automation.TreeScope]::Descendants
        } else {
            [System.Windows.Automation.TreeScope]::Children
        }
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            $scope,
            $condition)
        $window = $windows | Where-Object {
            !$Title -or $_.Current.Name -eq $Title
        } | Select-Object -First 1
        if ($window) { return $window }
        Start-Sleep -Milliseconds 125
    }
    throw "InnerTune did not expose the expected window '$Title'."
}

function Invoke-NamedButton(
    [System.Windows.Automation.AutomationElement]$Root,
    [string]$Name,
    [string]$AutomationId = ''
) {
    $conditions = [System.Collections.Generic.List[System.Windows.Automation.Condition]]::new()
    $conditions.Add((New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)))
    if ($AutomationId) {
        $conditions.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $AutomationId)))
    } else {
        $conditions.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name)))
    }
    $condition = New-Object System.Windows.Automation.AndCondition(
        $conditions.ToArray())
    $button = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition) | Where-Object { !$_.Current.IsOffscreen } | Select-Object -First 1
    if (!$button) { throw "Could not find the '$Name' button." }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

$testRoot = Join-Path $env:TEMP ("InnerTunePlaylistShareTest-" + [Guid]::NewGuid().ToString('N'))
$testData = Join-Path $testRoot 'data'
New-Item $testData -ItemType Directory -Force | Out-Null
Copy-Item $LibraryPath (Join-Path $testData 'library.json')
$libraryBefore = Get-Content (Join-Path $testData 'library.json') -Raw | ConvertFrom-Json
$queueBefore = @($libraryBefore.queue | ForEach-Object { $_.id }) -join '|'
$savedBefore = @($libraryBefore.savedQueues | ForEach-Object {
    "$($_.id):$($_.name):$(@($_.tracks | ForEach-Object { $_.id }) -join ',')"
}) -join '|'
$previousTestMode = $env:INNERTUNE_TEST_MODE
$previousTestInstance = $env:INNERTUNE_TEST_INSTANCE
$previousDataDirectory = $env:ITMUSIC_DATA_DIR
$process = $null

try {
    $env:INNERTUNE_TEST_MODE = '1'
    $env:INNERTUNE_TEST_INSTANCE = [Guid]::NewGuid().ToString('N')
    $env:ITMUSIC_DATA_DIR = $testData
    $process = Start-Process $ApplicationPath -ArgumentList 'test:share-current' -PassThru
    $main = Wait-ForWindow $process
    $prompt = Wait-ForWindow $process 'Share playlist'
    $promptBounds = $prompt.Current.BoundingRectangle
    if ($promptBounds.Left -gt -30000 -or $promptBounds.Top -gt -30000) {
        throw "The test prompt was not off-screen: $promptBounds"
    }
    Invoke-NamedButton $prompt 'Copy link'

    $clipboardPath = Join-Path $testData 'test-clipboard.txt'
    for ($attempt = 0; $attempt -lt 40 -and !(Test-Path $clipboardPath); $attempt++) {
        Start-Sleep -Milliseconds 100
    }
    if (!(Test-Path $clipboardPath)) { throw 'Share did not write the isolated test clipboard.' }
    $link = (Get-Content $clipboardPath -Raw).Trim()
    if (!$link.StartsWith('innertune://playlist/v1/')) { throw 'Share produced an invalid link prefix.' }

    $libraryAfter = Get-Content (Join-Path $testData 'library.json') -Raw | ConvertFrom-Json
    $queueAfter = @($libraryAfter.queue | ForEach-Object { $_.id }) -join '|'
    $savedAfter = @($libraryAfter.savedQueues | ForEach-Object {
        "$($_.id):$($_.name):$(@($_.tracks | ForEach-Object { $_.id }) -join ',')"
    }) -join '|'
    if ($queueBefore -ne $queueAfter -or $savedBefore -ne $savedAfter) {
        throw 'Sharing unexpectedly changed the queue or saved playlists.'
    }

    [pscustomobject]@{
        ShareDialogOffscreen = $true
        LinkPrefix = 'innertune://playlist/v1/'
        LinkLength = $link.Length
        LibraryUnchanged = $true
        QueueCount = @($libraryAfter.queue).Count
        SavedQueueCount = @($libraryAfter.savedQueues).Count
        TestClipboardIsolated = $true
        ProcessResponding = $process.Responding
        TestData = $testRoot
    } | ConvertTo-Json -Compress
}
finally {
    if ($process -and !$process.HasExited) {
        try {
            $main = Wait-ForWindow $process 'InnerTune'
            $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
            $process.WaitForExit(5000) | Out-Null
        }
        catch { }
        if (!$process.HasExited) { Stop-Process -Id $process.Id -Force }
    }
    $env:INNERTUNE_TEST_MODE = $previousTestMode
    $env:INNERTUNE_TEST_INSTANCE = $previousTestInstance
    $env:ITMUSIC_DATA_DIR = $previousDataDirectory
    if (!$KeepTestData -and (Test-Path $testRoot)) { Remove-Item $testRoot -Recurse -Force }
}
