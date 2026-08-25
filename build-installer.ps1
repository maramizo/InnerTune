param(
    [string]$OutputDirectory = "$PSScriptRoot\artifacts",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'
$version = '1.1.1'
$staging = Join-Path $env:TEMP "InnerTune-Installer-$version"
$payload = Join-Path $staging 'payload'
$installerScript = Join-Path $PSScriptRoot 'installer\InnerTune.iss'
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw 'Inno Setup 6 is required. Install it with: winget install --id JRSoftware.InnoSetup --exact'
}

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
New-Item $payload -ItemType Directory -Force | Out-Null
New-Item $OutputDirectory -ItemType Directory -Force | Out-Null

dotnet publish "$PSScriptRoot\InnerTune.Windows.csproj" `
    -c $Configuration -r win-x64 --self-contained true -o $payload

Copy-Item "$PSScriptRoot\README.md" $payload -Force

$provider = Join-Path $payload 'provider'
Push-Location $provider
try {
    npm.cmd ci --omit=dev --no-audit --no-fund
}
finally {
    Pop-Location
}

$runtime = Join-Path $payload 'runtime'
New-Item $runtime -ItemType Directory -Force | Out-Null
$node = (Get-Command node.exe -ErrorAction Stop).Source
$ffmpeg = (Get-Command ffmpeg.exe -ErrorAction Stop).Source
Copy-Item $node (Join-Path $runtime 'node.exe') -Force
Copy-Item $ffmpeg (Join-Path $runtime 'ffmpeg.exe') -Force

& $iscc "/DSourceDir=$payload" "/DOutputDir=$OutputDirectory" $installerScript
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

$artifact = Join-Path $OutputDirectory "InnerTune-Setup-$version.exe"
Get-Item $artifact | Select-Object FullName, Length, LastWriteTime
