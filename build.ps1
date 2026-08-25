param([string]$Output = "$PSScriptRoot\publish")
$ErrorActionPreference = 'Stop'
dotnet publish "$PSScriptRoot\InnerTune.Windows.csproj" -c Release -r win-x64 --self-contained false -o $Output
Copy-Item "$PSScriptRoot\setup.ps1" "$Output\setup.ps1" -Force
Write-Host "Built InnerTune at $Output"
