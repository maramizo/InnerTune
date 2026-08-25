param([string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent))

$ErrorActionPreference = 'Stop'
$projects = @(
    (Join-Path $RepositoryRoot 'InnerTune.Windows.csproj')
) + @(Get-ChildItem (Join-Path $RepositoryRoot 'diagnostics') -Filter '*.csproj' -Recurse |
    Select-Object -ExpandProperty FullName)

foreach ($project in $projects) {
    Write-Host "Checking formatting: $project"
    dotnet format $project --verify-no-changes --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Formatting check failed: $project" }
}
