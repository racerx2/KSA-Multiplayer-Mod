# Build-And-Deploy.ps1
# Builds the KSA Multiplayer Mod and deploys to KSA mods folder

param(
    [string]$Configuration = "Release",
    [string]$KSAPath = "C:\Program Files\Kitten Space Agency",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$ModName = "Multiplayer"
$SourceDir = $PSScriptRoot
$OutputDir = Join-Path $SourceDir "bin\$Configuration"
$ContentDir = Join-Path $KSAPath "Content"
$TargetModDir = Join-Path $ContentDir $ModName
$PackageDir = "C:\temp\KSA-Multiplayer-Package\Content\Multiplayer"

Write-Host "================================" -ForegroundColor Cyan
Write-Host "KSA Multiplayer Mod Deployment" -ForegroundColor Cyan  
Write-Host "================================" -ForegroundColor Cyan

# Build
if (-not $SkipBuild) {
    Write-Host "`n[1/6] Building mod..." -ForegroundColor Yellow
    Push-Location $SourceDir
    try {
        dotnet clean --configuration $Configuration --verbosity quiet
        dotnet build --configuration $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed!"
        }
        Write-Host "Build successful!" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}
else {
    Write-Host "`n[1/6] Skipping build (--SkipBuild specified)" -ForegroundColor Gray
}

# Create mod directory
Write-Host "`n[2/6] Creating mod directory..." -ForegroundColor Yellow
if (-not (Test-Path $TargetModDir)) {
    New-Item -ItemType Directory -Path $TargetModDir -Force | Out-Null
    Write-Host "Created: $TargetModDir" -ForegroundColor Green
}
else {
    Write-Host "Directory exists: $TargetModDir" -ForegroundColor Gray
}

# Copy files
Write-Host "`n[3/6] Copying mod files..." -ForegroundColor Yellow

$ModFilesToCopy = @(
    @{ Source = "$OutputDir\KSA.Mods.Multiplayer.dll"; Required = $true },
    @{ Source = "$OutputDir\KSA.Mods.Multiplayer.pdb"; Required = $false },
    @{ Source = "$SourceDir\mod.toml"; Required = $true }
)

foreach ($file in $ModFilesToCopy) {
    if (Test-Path $file.Source) {
        $fileName = Split-Path $file.Source -Leaf
        $destPath = Join-Path $TargetModDir $fileName
        Copy-Item -Path $file.Source -Destination $destPath -Force
        Write-Host "  Copied: $fileName" -ForegroundColor Green
    }
    elseif ($file.Required) {
        throw "Required file not found: $($file.Source)"
    }
    else {
        Write-Host "  Skipped (not found): $(Split-Path $file.Source -Leaf)" -ForegroundColor Gray
    }
}

# Clear old logs
Write-Host "`n[4/6] Clearing old logs..." -ForegroundColor Yellow
$LogsDir = Join-Path $TargetModDir "logs"
if (Test-Path $LogsDir) {
    Remove-Item -Path "$LogsDir\*" -Force -ErrorAction SilentlyContinue
    Write-Host "  Cleared logs in: $LogsDir" -ForegroundColor Green
}
else {
    Write-Host "  No logs directory found (will be created on first run)" -ForegroundColor Gray
}

# Copy to package directory
Write-Host "`n[5/6] Copying to package directory..." -ForegroundColor Yellow
if (-not (Test-Path $PackageDir)) {
    New-Item -ItemType Directory -Path $PackageDir -Force | Out-Null
    Write-Host "  Created: $PackageDir" -ForegroundColor Green
}
$PackageFiles = @(
    @{ Source = "$OutputDir\KSA.Mods.Multiplayer.dll"; Required = $true },
    @{ Source = "$SourceDir\mod.toml"; Required = $true }
)
foreach ($file in $PackageFiles) {
    if (Test-Path $file.Source) {
        $fileName = Split-Path $file.Source -Leaf
        $destPath = Join-Path $PackageDir $fileName
        Copy-Item -Path $file.Source -Destination $destPath -Force
        Write-Host "  Copied: $fileName" -ForegroundColor Green
    }
}

# Create desktop shortcut with Run as Administrator
Write-Host "`n[6/6] Creating desktop shortcut..." -ForegroundColor Yellow
$DesktopPath = [Environment]::GetFolderPath("Desktop")
$ShortcutPath = Join-Path $DesktopPath "KSA Multiplayer.lnk"
$TargetExe = Join-Path $KSAPath "KSA.ModLoader.exe"

if (Test-Path $TargetExe) {
    # Create shortcut using WScript.Shell
    $WshShell = New-Object -ComObject WScript.Shell
    $Shortcut = $WshShell.CreateShortcut($ShortcutPath)
    $Shortcut.TargetPath = $TargetExe
    $Shortcut.WorkingDirectory = $KSAPath
    $Shortcut.Description = "KSA with Multiplayer Mod (Admin)"
    $Shortcut.Save()
    
    # Set "Run as Administrator" flag by modifying the .lnk file bytes
    $bytes = [System.IO.File]::ReadAllBytes($ShortcutPath)
    $bytes[0x15] = $bytes[0x15] -bor 0x20  # Set RUNASADMIN flag
    [System.IO.File]::WriteAllBytes($ShortcutPath, $bytes)
    
    Write-Host "  Created: $ShortcutPath (Run as Admin)" -ForegroundColor Green
}
else {
    Write-Host "  Warning: KSA.ModLoader.exe not found at $TargetExe" -ForegroundColor Yellow
}

# Summary
Write-Host "`n================================" -ForegroundColor Cyan
Write-Host "Deployment Complete!" -ForegroundColor Green
Write-Host "================================" -ForegroundColor Cyan
Write-Host "`nMod installed to: $TargetModDir"
Write-Host "`nFiles deployed:"
Get-ChildItem $TargetModDir | ForEach-Object { Write-Host "  - $($_.Name)" }

Write-Host "`nDesktop shortcut: $ShortcutPath (runs as admin)"

Write-Host "`nTo use the mod:" -ForegroundColor Yellow
Write-Host "  1. Launch via 'KSA Multiplayer' desktop shortcut"
Write-Host "  2. Open console (~)"
Write-Host "  3. Type 'mp_host <name> 7777 8' to host"
Write-Host "  4. Or 'mp_join <name> localhost 7777' to join"
