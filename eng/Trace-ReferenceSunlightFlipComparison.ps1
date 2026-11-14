param(
    [Parameter(Mandatory)][string]$Assembly,
    [Parameter(Mandatory)][string]$PresentMon,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('composed','independent')][string[]]$Modes = @('composed','independent')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$client = (Resolve-Path -LiteralPath $Assembly).Path
$capture = (Resolve-Path -LiteralPath $PresentMon).Path
$traceScript = Join-Path $PSScriptRoot 'Trace-ReferenceSunlight.ps1'
$root = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
foreach ($mode in $Modes) {
    $result = Join-Path $root $mode
    Write-Host "[AUFNAHME] $mode | Schatten an und aus"
    & pwsh -NoProfile -File $traceScript -Assembly $client -PresentMon $capture -FlipMode $mode -OutputDirectory $result
    if (!(Test-Path -LiteralPath (Join-Path $result 'wp15-gpu.etl')) -or
        !(Test-Path -LiteralPath (Join-Path $result 'replays/summary.csv'))) {
        throw "Incomplete capture in $result."
    }
    if ($LASTEXITCODE -ne 0) { Write-Warning "Budget check reported a failure in $mode. Evidence was retained." }
}
if ($Modes.Count -eq 2) { Write-Host "[BEIDE AUFNAHMEN FERTIG] $root" }
else { Write-Host "[AUFNAHME FERTIG] $root" }
