[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

$replacements = @(
    @{
        Path = 'Client\src\ModInfo.cs'
        Pattern = 'public const string Version = "[^"]+";'
        Value = "public const string Version = `"$Version`";"
    },
    @{
        Path = 'Client\mod.toml'
        Pattern = '(?m)^version\s*=\s*"[^"]+"'
        Value = "version = `"$Version`""
    },
    @{
        Path = 'KSA-Multiplayer-Package\Content\Multiplayer\mod.toml'
        Pattern = '(?m)^version\s*=\s*"[^"]+"'
        Value = "version = `"$Version`""
    },
    @{
        Path = 'Client\KSA-Multiplayer-Mod.csproj'
        Pattern = '<Version>[^<]+</Version>'
        Value = "<Version>$Version</Version>"
    },
    @{
        Path = 'Server\KSA-Dedicated-Server.csproj'
        Pattern = '<Version>[^<]+</Version>'
        Value = "<Version>$Version</Version>"
    }
)

foreach ($replacement in $replacements) {
    $path = Join-Path $repositoryRoot $replacement.Path
    $content = Get-Content -LiteralPath $path -Raw
    $updated = $content -replace $replacement.Pattern, $replacement.Value
    if ($updated -eq $content) {
        throw "Version marker not found in $($replacement.Path)"
    }
    $normalized = $updated.TrimEnd("`r", "`n") + [Environment]::NewLine
    [IO.File]::WriteAllText($path, $normalized, [Text.UTF8Encoding]::new($false))
}

Write-Host "KSA Multiplayer version updated to $Version"
