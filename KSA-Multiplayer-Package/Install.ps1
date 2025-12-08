# KSA Multiplayer Mod - Install Script

param(
    [string]$KSAPath = "C:\Program Files\Kitten Space Agency"
)

$ErrorActionPreference = "Stop"

# Check for admin privileges
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Requesting administrator privileges..." -ForegroundColor Yellow
    $scriptPath = $MyInvocation.MyCommand.Path
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -KSAPath `"$KSAPath`""
    Start-Process PowerShell -Verb RunAs -ArgumentList $arguments
    exit
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  KSA Multiplayer Mod Installer" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check KSA installation
if (-not (Test-Path $KSAPath)) {
    Write-Host "ERROR: KSA not found at $KSAPath" -ForegroundColor Red
    Write-Host "Use -KSAPath parameter to specify correct location" -ForegroundColor Yellow
    Read-Host "Press Enter to exit"
    exit 1
}

if (-not (Test-Path "$KSAPath\KSA.exe")) {
    Write-Host "ERROR: KSA.exe not found in $KSAPath" -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

Write-Host "KSA found at: $KSAPath" -ForegroundColor Green
Write-Host ""

$ScriptDir = $PSScriptRoot

# Step 1: Install Launcher folder
Write-Host "[1/4] Installing ModLoader to Launcher folder..." -ForegroundColor Yellow

$LauncherDir = "$KSAPath\Launcher"
if (-not (Test-Path $LauncherDir)) {
    New-Item -ItemType Directory -Path $LauncherDir -Force | Out-Null
    Write-Host "  Created $LauncherDir" -ForegroundColor Gray
}

Copy-Item "$ScriptDir\Launcher\KSA.ModLoader.exe" "$LauncherDir\KSA.ModLoader.exe" -Force
Write-Host "  Installed KSA.ModLoader.exe" -ForegroundColor Gray

Copy-Item "$ScriptDir\Launcher\KSA.ModLoader.dll" "$LauncherDir\KSA.ModLoader.dll" -Force
Write-Host "  Installed KSA.ModLoader.dll" -ForegroundColor Gray

Copy-Item "$ScriptDir\Launcher\KSA.ModLoader.deps.json" "$LauncherDir\KSA.ModLoader.deps.json" -Force
Write-Host "  Installed KSA.ModLoader.deps.json" -ForegroundColor Gray

Copy-Item "$ScriptDir\Launcher\KSA.ModLoader.runtimeconfig.json" "$LauncherDir\KSA.ModLoader.runtimeconfig.json" -Force
Write-Host "  Installed KSA.ModLoader.runtimeconfig.json" -ForegroundColor Gray

Copy-Item "$ScriptDir\Launcher\0Harmony.dll" "$LauncherDir\0Harmony.dll" -Force
Write-Host "  Installed 0Harmony.dll" -ForegroundColor Gray

Copy-Item "$ScriptDir\Launcher\KSA.ModLoader.cmd" "$LauncherDir\KSA.ModLoader.cmd" -Force
Write-Host "  Installed KSA.ModLoader.cmd" -ForegroundColor Gray

Write-Host "  Done" -ForegroundColor Green

# Step 2: Install Multiplayer mod
Write-Host ""
Write-Host "[2/4] Installing Multiplayer mod..." -ForegroundColor Yellow

$ModDir = "$KSAPath\Content\Multiplayer"
if (-not (Test-Path $ModDir)) {
    New-Item -ItemType Directory -Path $ModDir -Force | Out-Null
    Write-Host "  Created $ModDir" -ForegroundColor Gray
}

Copy-Item "$ScriptDir\Content\Multiplayer\KSA.Mods.Multiplayer.dll" "$ModDir\KSA.Mods.Multiplayer.dll" -Force
Write-Host "  Installed KSA.Mods.Multiplayer.dll" -ForegroundColor Gray

Copy-Item "$ScriptDir\Content\Multiplayer\mod.toml" "$ModDir\mod.toml" -Force
Write-Host "  Installed mod.toml" -ForegroundColor Gray

# Create logs directory
$LogsDir = "$ModDir\logs"
if (-not (Test-Path $LogsDir)) {
    New-Item -ItemType Directory -Path $LogsDir -Force | Out-Null
    Write-Host "  Created logs directory" -ForegroundColor Gray
}

Write-Host "  Done" -ForegroundColor Green

# Step 3: Update manifest.toml
Write-Host ""
Write-Host "[3/4] Updating manifest.toml..." -ForegroundColor Yellow

$ManifestPath = "$KSAPath\Content\manifest.toml"
if (Test-Path $ManifestPath) {
    $manifestContent = Get-Content $ManifestPath -Raw
    if ($manifestContent -notmatch '\[\[mods\]\][\s\S]*?id\s*=\s*"Multiplayer"') {
        $newEntry = "`n`n[[mods]]`nid = `"Multiplayer`"`nenabled = true"
        Add-Content -Path $ManifestPath -Value $newEntry
        Write-Host "  Added Multiplayer entry to manifest" -ForegroundColor Gray
    } else {
        Write-Host "  Multiplayer already in manifest" -ForegroundColor Gray
    }
} else {
    Write-Host "  WARNING: manifest.toml not found, you may need to enable the mod manually" -ForegroundColor Yellow
}

Write-Host "  Done" -ForegroundColor Green

# Step 4: Create desktop shortcut
Write-Host ""
Write-Host "[4/4] Creating desktop shortcut..." -ForegroundColor Yellow

$DesktopPath = [Environment]::GetFolderPath("Desktop")
$ShortcutPath = "$DesktopPath\KSA with Mods.lnk"

$WScriptShell = New-Object -ComObject WScript.Shell
$Shortcut = $WScriptShell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath = "$KSAPath\Launcher\KSA.ModLoader.cmd"
$Shortcut.WorkingDirectory = $KSAPath
$Shortcut.Description = "Launch KSA with Multiplayer Mod"
$Shortcut.Save()

Write-Host "  Created: $ShortcutPath" -ForegroundColor Gray
Write-Host "  Done" -ForegroundColor Green

# Summary
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Installation Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "To play:" -ForegroundColor Cyan
Write-Host "  1. Use 'KSA with Mods' shortcut on desktop"
Write-Host "  2. In-game, press ~ for console"
Write-Host "  3. mp_host <n> 7777 8  - to host"
Write-Host "  4. mp_join <n> <ip> 7777  - to join"
Write-Host ""
Read-Host "Press Enter to exit"
