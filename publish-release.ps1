param(
    [string]$Repository = 'maramizo/InnerTune',
    [string]$ArtifactDirectory = "$PSScriptRoot\artifacts",
    [switch]$SkipBuild,
    [switch]$Draft,
    [string]$Notes
)

$ErrorActionPreference = 'Stop'
$project = [xml](Get-Content (Join-Path $PSScriptRoot 'InnerTune.Windows.csproj'))
$version = [string]$project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'The project version is missing.' }
$tag = "v$version"
$installer = Join-Path $ArtifactDirectory "InnerTune-Setup-$version.exe"
$checksum = "$installer.sha256"

$nativeGh = Get-Command gh -ErrorAction SilentlyContinue
$useWslGh = $false
$wslTarget = @()
if ($PSScriptRoot -match '^\\\\wsl(?:\.localhost|\$)\\(?<distribution>[^\\]+)\\home\\(?<user>[^\\]+)\\') {
    $wslTarget = @('-d', $Matches.distribution, '-u', $Matches.user)
}
if (-not $nativeGh -and (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
    & wsl.exe @wslTarget sh -lc 'command -v gh >/dev/null 2>&1'
    $useWslGh = $LASTEXITCODE -eq 0
}
if (-not $nativeGh -and -not $useWslGh) { throw 'GitHub CLI (gh) is required in Windows or WSL to publish a release.' }

function Invoke-GitHubCli([string[]]$Arguments) {
    if ($useWslGh) { & wsl.exe @wslTarget gh @Arguments }
    else { & $nativeGh.Source @Arguments }
}

Invoke-GitHubCli @('auth', 'status') | Out-Null
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build-installer.ps1') -OutputDirectory $ArtifactDirectory
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed with exit code $LASTEXITCODE." }
}
if (-not (Test-Path $installer)) { throw "Installer not found: $installer" }

$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $installer -Leaf)" | Set-Content $checksum -Encoding ascii -NoNewline
$assets = @($installer, $checksum)
$publishAssets = if ($useWslGh) {
    @($assets | ForEach-Object {
        $portablePath = $_.Replace('\', '/')
        (& wsl.exe @wslTarget wslpath -a $portablePath).Trim()
    })
} else { $assets }

$releaseJson = Invoke-GitHubCli @('release', 'list', '--repo', $Repository, '--limit', '100', '--json', 'tagName')
if ($LASTEXITCODE -ne 0) { throw 'Could not list existing GitHub releases.' }
$releaseExists = @($releaseJson | ConvertFrom-Json | ForEach-Object { $_.tagName }) -contains $tag
if ($releaseExists) {
    Invoke-GitHubCli (@('release', 'upload', $tag) + $publishAssets + @('--repo', $Repository, '--clobber'))
}
else {
    $arguments = @('release', 'create', $tag) + $publishAssets + @('--repo', $Repository, '--target', 'main', '--title', "InnerTune $version")
    if ($Draft) { $arguments += '--draft' }
    if ([string]::IsNullOrWhiteSpace($Notes)) { $arguments += '--generate-notes' }
    else { $arguments += @('--notes', $Notes) }
    Invoke-GitHubCli $arguments
}
if ($LASTEXITCODE -ne 0) { throw "GitHub release publishing failed with exit code $LASTEXITCODE." }

Write-Host "Published $tag to https://github.com/$Repository/releases/tag/$tag"
Get-Item $installer, $checksum | Select-Object FullName, Length, LastWriteTime
