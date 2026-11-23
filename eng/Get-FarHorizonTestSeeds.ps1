$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$manifestPath = Join-Path $PSScriptRoot 'reference-worlds/far-horizon.v1.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if (($manifest.schemaVersion -isnot [int] -and $manifest.schemaVersion -isnot [long]) -or
    $manifest.schemaVersion -ne 1 -or $manifest.world -isnot [string] -or $manifest.world -cne 'far-horizon') {
    throw "Unsupported reference world manifest: $manifestPath"
}

$seeds = @($manifest.seed, $manifest.spawnFallbackSeed)
foreach ($seed in $seeds) {
    $parsed = [uint64]0
    if ($seed -isnot [string] -or $seed -cnotmatch '\A(?:0|[1-9][0-9]{0,19})\z' -or
        -not [uint64]::TryParse($seed, [ref]$parsed)) {
        throw "Far Horizon seeds must be canonical unsigned 64-bit decimal strings: $manifestPath"
    }
}
if ($seeds[0] -ceq $seeds[1]) {
    throw "Far Horizon requires a distinct spawn-fallback seed: $manifestPath"
}

$seeds
