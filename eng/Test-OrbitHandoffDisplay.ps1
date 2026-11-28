$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Load only the display analysis functions. The measurement entry point starts the client.
$tokens = $null
$errors = $null
$script = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Measure-OrbitTerrainPbr.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors -join "`n") }
foreach ($name in 'Parse-Milliseconds','Get-DisplayedPresent','Get-FlightHandoffDisplay','New-DisplayCrossingRow') {
    $definition = $script.Find({
        param($element)
        $element -is [Management.Automation.Language.FunctionDefinitionAst] -and $element.Name -ceq $name
    }, $false)
    if (!$definition) { throw "Missing display analysis function $name." }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$invariant = [Globalization.CultureInfo]::InvariantCulture
$frequency = 10000L
$crossing = [pscustomobject]@{
    Run='01-baseline'; Pair=1; Variant='baseline'; Phase='orbit'
    Step='flight'; Generation=2; StartQpc=20000L; EndQpc=22000L
}
function New-DisplayedRow([string]$Address, [long]$Cpu, [double]$Latency, [double]$Duration) {
    Get-DisplayedPresent ([pscustomobject]@{
        SwapChainAddress=$Address; CPUStartQPC=$Cpu
        DisplayLatency=$Latency.ToString($invariant); DisplayedTime=$Duration.ToString($invariant)
    }) $frequency
}

$old = @((New-DisplayedRow '0x01' 19400 20 17), (New-DisplayedRow '0x01' 19500 20 17),
    (New-DisplayedRow '0x01' 19800 30 17))
$new = @((New-DisplayedRow '0x02' 21700 50 17), (New-DisplayedRow '0x02' 21900 50 17))
$measured = Get-FlightHandoffDisplay $crossing ($old + $new) $frequency 1000 40000
if ($measured.Status -cne 'OBSERVED' -or $measured.LastOldDisplayQpc -ne 20100 -or
    $measured.FirstNewDisplayQpc -ne 22200 -or $measured.HandoffDisplayIntervalMs -ne 210 -or
    $measured.UnclassifiedGapMs -ne 193) { throw 'Expected a displayed bracket extending beyond the first renderer receipt.' }
$flightRow = New-DisplayCrossingRow $crossing $null $measured.Status $measured.Reason $measured
$surfaceRow = New-DisplayCrossingRow $crossing 27.3 'OBSERVED' $null $null
if ($flightRow.HandoffDisplayIntervalMs -ne 210 -or $flightRow.FirstNewDisplayQpc -ne 22200 -or
    $surfaceRow.LongestOverlappingDisplayIntervalMs -ne 27.3 -or $null -ne $surfaceRow.FirstNewDisplayQpc) {
    throw 'Expected separate flight and surface columns in the crossing CSV.'
}

$reused = @((New-DisplayedRow '0x001' 21700 50 17), (New-DisplayedRow '0x1' 21900 50 17))
$blocked = Get-FlightHandoffDisplay $crossing ($old + $reused) $frequency 1000 40000
if ($blocked.Status -cne 'BLOCKED' -or $blocked.Reason -notlike 'Repeated swap-chain address*' -or
    $null -ne $blocked.HandoffDisplayIntervalMs) { throw 'A recycled address must fail closed.' }

$duplicates = @($old[1], $old[1], $new[0], $new[0])
$blocked = Get-FlightHandoffDisplay $crossing $duplicates $frequency 1000 40000
if ($blocked.Status -cne 'BLOCKED' -or $blocked.Reason -notlike 'No stable displayed swap chain*') {
    throw 'A repeated ETW row cannot corroborate renderer identity.'
}

$interleaved = $old + $new + (New-DisplayedRow '0x01' 21800 50 17)
$blocked = Get-FlightHandoffDisplay $crossing $interleaved $frequency 1000 40000
if ($blocked.Status -cne 'BLOCKED') { throw 'Interleaved old and new swap-chain displays must fail closed.' }

$blocked = Get-FlightHandoffDisplay $crossing ($old + $new) $frequency 1000 25000
if ($blocked.Status -cne 'BLOCKED' -or $blocked.Reason -notlike 'Incomplete capture*') {
    throw 'A finite capture ending in the ownership context must fail closed.'
}

$dropped = Get-DisplayedPresent ([pscustomobject]@{ CPUStartQPC=21800L; DisplayLatency='NA'; DisplayedTime='NA' }) $frequency
if ($null -ne $dropped) { throw 'A dropped present has no proven display onset.' }
Write-Host 'PASS: Displayed renderer handoff bracket, recycled address, interleaving, capture boundary and dropped presents.'
