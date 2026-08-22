[CmdletBinding()]
param(
    [string]$GameDirectory = 'C:\Program Files\Kitten Space Agency'
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdministrator) {
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`"",
        '-GameDirectory', "`"$GameDirectory`""
    )
    $process = Start-Process powershell.exe -Verb RunAs -Wait -PassThru `
        -ArgumentList $arguments
    exit $process.ExitCode
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceDirectory = Join-Path $repositoryRoot 'KSA-Multiplayer-Package\Content\Multiplayer'
$destinationDirectory = Join-Path $GameDirectory 'Content\Multiplayer'

New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceDirectory 'Multiplayer.dll') `
    -Destination $destinationDirectory -Force
Copy-Item -LiteralPath (Join-Path $sourceDirectory 'mod.toml') `
    -Destination $destinationDirectory -Force

Write-Host "Installed KSA Multiplayer into $destinationDirectory"
