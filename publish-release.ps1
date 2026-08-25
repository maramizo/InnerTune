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

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) is required to publish a release.' }
gh auth status | Out-Null
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build-installer.ps1') -OutputDirectory $ArtifactDirectory
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed with exit code $LASTEXITCODE." }
}
if (-not (Test-Path $installer)) { throw "Installer not found: $installer" }

$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $installer -Leaf)" | Set-Content $checksum -Encoding ascii -NoNewline
$assets = @($installer, $checksum)

gh release view $tag --repo $Repository *> $null
if ($LASTEXITCODE -eq 0) {
    gh release upload $tag @assets --repo $Repository --clobber
}
else {
    $arguments = @('release', 'create', $tag) + $assets + @('--repo', $Repository, '--target', 'main', '--title', "InnerTune $version")
    if ($Draft) { $arguments += '--draft' }
    if ([string]::IsNullOrWhiteSpace($Notes)) { $arguments += '--generate-notes' }
    else { $arguments += @('--notes', $Notes) }
    & gh @arguments
}
if ($LASTEXITCODE -ne 0) { throw "GitHub release publishing failed with exit code $LASTEXITCODE." }

Write-Host "Published $tag to https://github.com/$Repository/releases/tag/$tag"
Get-Item $installer, $checksum | Select-Object FullName, Length, LastWriteTime
