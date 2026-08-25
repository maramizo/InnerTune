param([switch]$Launch)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$provider = Join-Path $root 'provider'

if (-not (Get-Command node.exe -ErrorAction SilentlyContinue)) {
  throw 'Node.js was not found. Install Node.js 22 or later, then run setup.ps1 again.'
}
if (-not (Get-Command codex -ErrorAction SilentlyContinue)) {
  Write-Warning 'Codex CLI was not found. Playback and search will work; the AI tab will require Codex CLI.'
}
if (-not (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue)) {
  throw 'FFmpeg was not found. Install FFmpeg with "winget install Gyan.FFmpeg", then run setup.ps1 again.'
}

Write-Host 'Installing the local InnerTube provider...'
Push-Location $provider
try { npm.cmd install --omit=dev --no-audit --no-fund }
finally { Pop-Location }

$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop 'InnerTune.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $root 'InnerTune.exe'
$shortcut.WorkingDirectory = $root
$shortcut.Description = 'InnerTune local music player'
$shortcut.Save()

$startup = [Environment]::GetFolderPath('Startup')
$startupShortcut = $shell.CreateShortcut((Join-Path $startup 'InnerTune.lnk'))
$startupShortcut.TargetPath = Join-Path $root 'InnerTune.exe'
$startupShortcut.WorkingDirectory = $root
$startupShortcut.Description = 'Start InnerTune in the Windows notification area'
$startupShortcut.Save()

$protocol = 'HKCU:\Software\Classes\innertune'
$command = Join-Path $protocol 'shell\open\command'
New-Item $protocol -Force | Out-Null
Set-Item $protocol -Value 'URL:InnerTune Playlist Protocol'
New-ItemProperty $protocol -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null
New-Item (Join-Path $protocol 'DefaultIcon') -Force | Out-Null
Set-Item (Join-Path $protocol 'DefaultIcon') -Value "`"$(Join-Path $root 'InnerTune.exe')`",0"
New-Item $command -Force | Out-Null
Set-Item $command -Value "`"$(Join-Path $root 'InnerTune.exe')`" `"%1`""

Write-Host "Ready. Desktop/startup shortcuts and innertune:// playlist links are configured."
if ($Launch) { Start-Process (Join-Path $root 'InnerTune.exe') }
