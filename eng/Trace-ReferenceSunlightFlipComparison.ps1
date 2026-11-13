param(
    [Parameter(Mandatory)][string]$Assembly,
    [Parameter(Mandatory)][string]$PresentMon,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$client = (Resolve-Path -LiteralPath $Assembly).Path
$capture = (Resolve-Path -LiteralPath $PresentMon).Path
$traceScript = Join-Path $PSScriptRoot 'Trace-ReferenceSunlight.ps1'
$root = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
foreach ($mode in 'composed','independent') {
    $result = Join-Path $root $mode
    Write-Host "[AUFNAHME] $mode | Schatten an und aus"
    & pwsh -NoProfile -File $traceScript -Assembly $client -PresentMon $capture -FlipMode $mode -OutputDirectory $result
    if (!(Test-Path -LiteralPath (Join-Path $result 'presents.csv')) -or
        !(Test-Path -LiteralPath (Join-Path $result 'replays/summary.csv'))) {
        throw "Incomplete capture in $result."
    }
    if ($LASTEXITCODE -ne 0) { Write-Warning "Budget check reported a failure in $mode. Evidence was retained." }
}
Write-Host "[BEIDE AUFNAHMEN FERTIG] $root"
