param(
    [string]$CacheDirectory = (Join-Path $env:LOCALAPPDATA 'InnerTune\audio-cache'),
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$probe = Join-Path $PSScriptRoot 'SettingsProbe\SettingsProbe.csproj'
$catalog = [ordered]@{
    'vIOO_7DLr3M' = @{ Title = 'BURN IT DOWN'; Expected = 'jump' }
    'KFA1hM91ffo' = @{ Title = 'In the End'; Expected = 'jump' }
    '8ZBnwBVjwOk' = @{ Title = 'New Divide'; Expected = 'jump' }
    'KwN_f0fTHoE' = @{ Title = 'One Step Closer'; Expected = 'jump' }
    'yOW7Eh81Gto' = @{ Title = 'Faint'; Expected = 'jump' }
    '6xzN8Nt0Pok' = @{ Title = 'I Wanna Dance with Somebody'; Expected = 'jump' }
    'yYfzC8cY4j8' = @{ Title = 'Rhythm Is a Dancer'; Expected = 'jump' }
    '5Rk8u2FTaG0' = @{ Title = 'Sandstorm'; Expected = 'jump' }
    '32J7bZHva9M' = @{ Title = 'Da Funk'; Expected = 'jump' }
    'DMJgiky90pE' = @{ Title = 'Children (Dream Version)'; Expected = 'jump' }
    'Bo68RTmLd6M' = @{ Title = 'Strobe (Dimension Remix)'; Expected = 'jump' }
    'gt1pKrwxAJU' = @{ Title = 'Opus'; Expected = 'grounded' }
    '5w3rRFWzjcM' = @{ Title = 'Sweden'; Expected = 'grounded' }
    'gLgUesz8444' = @{ Title = 'An Ending'; Expected = 'grounded' }
    'Jd8w8iPWGM8' = @{ Title = 'Home (Music Box)'; Expected = 'grounded' }
    'AvDrW4JTjME' = @{ Title = 'Fallen Down (Reprise)'; Expected = 'grounded' }
    'sJhnVunhNZY' = @{ Title = 'Dire Dire Docks'; Expected = 'grounded' }
    'K892qn3U524' = @{ Title = "Ezio's Family"; Expected = 'grounded' }
    'H1LdQntDnFY' = @{ Title = 'One More Light'; Expected = 'grounded' }
    'Lp-TdtDYGAA' = @{ Title = "At Doom's Gate"; Expected = 'grounded' }
    '813-3iL5OsE' = @{ Title = 'Dragonborn'; Expected = 'grounded' }
    '1RVAJ2ZPTFQ' = @{ Title = 'Ocarina Of Time'; Expected = 'grounded' }
    'EIaLT43HX9o' = @{ Title = 'The Legend Of Zelda - Main Theme'; Expected = 'grounded' }
}

$available = @()
foreach ($entry in $catalog.GetEnumerator()) {
    $path = Join-Path $CacheDirectory "$($entry.Key).playable.m4a"
    if (Test-Path $path) { $available += $path }
}
if ($available.Count -eq 0) { throw "No catalog tracks were found in $CacheDirectory." }

Push-Location $root
try {
    $json = dotnet run --project $probe -c $Configuration -- @available | ConvertFrom-Json
}
finally {
    Pop-Location
}

$failed = $false
$rows = foreach ($track in $json.inspectedTracks) {
    $id = [IO.Path]::GetFileName($track.path) -replace '\.playable\.m4a$', ''
    $expected = $catalog[$id].Expected
    $windowCount = @($track.analysis.JumpWindows).Count
    $actual = if ($windowCount -gt 0) { 'jump' } else { 'grounded' }
    if ($actual -ne $expected) { $failed = $true }
    [pscustomobject]@{
        Title = $catalog[$id].Title
        Expected = $expected
        Actual = $actual
        Windows = $windowCount
        Coverage = '{0:P1}' -f $track.jumpCoverage
    }
}
$rows | Format-Table -AutoSize
if (-not $json.passed -or $failed) { throw 'Motion catalog validation failed.' }
