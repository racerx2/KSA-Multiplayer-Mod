[CmdletBinding()]
param(
    [string]$GameDirectory = 'C:\Program Files\Kitten Space Agency',
    [string]$StarMapDirectory = 'C:\Program Files (x86)\StarMap',
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
$starMapVersion = '0.3.0'
$starMapSha256 = '545BC9E7B6D1AA6466959AF175518B9D388CDE4C0143175949A267D07FD968B8'
$dotnetRuntimeVersion = '10.0.10'
$repoRoot = Split-Path -Parent $PSScriptRoot
$packageMod = Join-Path $repoRoot 'KSA-Multiplayer-Package\Content\Multiplayer'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`"",
        '-GameDirectory', "`"$GameDirectory`"",
        '-StarMapDirectory', "`"$StarMapDirectory`""
    )
    if ($Launch) { $arguments += '-Launch' }
    $process = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList $arguments
    exit $process.ExitCode
}

if (-not (Test-Path (Join-Path $GameDirectory 'KSA.dll'))) {
    throw "KSA.dll was not found in '$GameDirectory'."
}
if (-not (Test-Path (Join-Path $packageMod 'Multiplayer.dll'))) {
    throw "The packaged Multiplayer.dll was not found. Pull or build the repository first."
}

$workDirectory = Join-Path ([IO.Path]::GetTempPath()) ("ksa-update-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $workDirectory | Out-Null

try {
    Write-Host "Installing KSA Multiplayer..."
    $modDestination = Join-Path $GameDirectory 'Content\Multiplayer'
    New-Item -ItemType Directory -Path $modDestination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $packageMod 'Multiplayer.dll') -Destination $modDestination -Force
    Copy-Item -LiteralPath (Join-Path $packageMod 'mod.toml') -Destination $modDestination -Force

    Write-Host "Installing pinned StarMap $starMapVersion..."
    $starMapZip = Join-Path $workDirectory 'StarMap.zip'
    $starMapExtract = Join-Path $workDirectory 'StarMap'
    $starMapUrl = "https://github.com/StarMapLoader/StarMap/releases/download/$starMapVersion/StarMapLauncher-$starMapVersion.zip"
    Invoke-WebRequest -Uri $starMapUrl -OutFile $starMapZip
    $actualHash = (Get-FileHash -LiteralPath $starMapZip -Algorithm SHA256).Hash
    if ($actualHash -ne $starMapSha256) {
        throw "StarMap archive hash mismatch. Expected $starMapSha256, received $actualHash."
    }
    Expand-Archive -LiteralPath $starMapZip -DestinationPath $starMapExtract
    New-Item -ItemType Directory -Path $StarMapDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $starMapExtract '*') -Destination $StarMapDirectory -Recurse -Force

    $privateDotnet = Join-Path $StarMapDirectory '.dotnet'
    $privateDotnetExe = Join-Path $privateDotnet 'dotnet.exe'
    $installedRuntime = Join-Path $privateDotnet "shared\Microsoft.NETCore.App\$dotnetRuntimeVersion"
    if (-not (Test-Path $installedRuntime)) {
        Write-Host "Installing private .NET runtime $dotnetRuntimeVersion..."
        $dotnetInstaller = Join-Path $workDirectory 'dotnet-install.ps1'
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $dotnetInstaller
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $dotnetInstaller `
            -Version $dotnetRuntimeVersion -Runtime dotnet -InstallDir $privateDotnet -NoPath
    }
    if (-not (Test-Path $privateDotnetExe)) {
        throw "Private .NET runtime installation failed."
    }

    [ordered]@{
        GameLocation = $GameDirectory.TrimEnd('\') + '\'
        RepositoryLocation = ''
        GameArguments = @()
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $StarMapDirectory 'StarMapConfig.json') -Encoding UTF8

    @'
@echo off
set "DOTNET_ROOT=%~dp0.dotnet"
set "DOTNET_MULTILEVEL_LOOKUP=0"
"%~dp0.dotnet\dotnet.exe" "%~dp0StarMap.Loader.dll"
'@ | Set-Content -LiteralPath (Join-Path $StarMapDirectory 'Launch-StarMap.cmd') -Encoding ASCII

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut('C:\Users\Public\Desktop\KSA with Mods.lnk')
    $shortcut.TargetPath = Join-Path $StarMapDirectory 'Launch-StarMap.cmd'
    $shortcut.WorkingDirectory = $StarMapDirectory
    $shortcut.Description = 'Launch Kitten Space Agency with managed multiplayer dependencies'
    $shortcut.Save()

    Write-Host "KSA Multiplayer and its pinned loader are ready."
    if ($Launch) {
        Start-Process -FilePath (Join-Path $StarMapDirectory 'Launch-StarMap.cmd') `
            -WorkingDirectory $StarMapDirectory
    }
}
finally {
    if (Test-Path $workDirectory) {
        Remove-Item -LiteralPath $workDirectory -Recurse -Force
    }
}
