$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $repoRoot "dist\YogaNotes.exe"
$iconPath = Join-Path $repoRoot "Assets\app-icon.ico"

if (-not (Test-Path $exePath)) {
    throw "Executable not found at $exePath. Build it first with dotnet publish."
}

$shell = New-Object -ComObject WScript.Shell

$desktopLink = Join-Path ([Environment]::GetFolderPath("Desktop")) "YogaNotes.lnk"
$shortcut = $shell.CreateShortcut($desktopLink)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = Split-Path -Parent $exePath
$shortcut.IconLocation = "$iconPath,0"
$shortcut.Save()

$startMenuDir = Join-Path ([Environment]::GetFolderPath("Programs")) "YogaNotes"
New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null
$startLink = Join-Path $startMenuDir "YogaNotes.lnk"
$smShortcut = $shell.CreateShortcut($startLink)
$smShortcut.TargetPath = $exePath
$smShortcut.WorkingDirectory = Split-Path -Parent $exePath
$smShortcut.IconLocation = "$iconPath,0"
$smShortcut.Save()

Write-Host "Created shortcuts:"
Write-Host "  Desktop:    $desktopLink"
Write-Host "  Start Menu: $startLink"
