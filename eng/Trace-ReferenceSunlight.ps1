param(
    [Parameter(Mandatory)][string]$Assembly,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('composed','independent')][string]$FlipMode,
    [string]$PresentMon
)

$ErrorActionPreference = 'Stop'
$assemblyPath = (Resolve-Path -LiteralPath $Assembly).Path
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell to record DxgKrnl.'
}
if ($FlipMode -and !$PresentMon) { throw 'Specify PresentMon to validate the flip-mode run.' }
$presentMonPath = if ($PresentMon) { (Resolve-Path -LiteralPath $PresentMon).Path }
$output = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$trace = Join-Path $output 'wp15-gpu.etl'
$replay = Join-Path $PSScriptRoot 'Measure-ReferenceSunlight.ps1'
& wpr -start GPU -filemode
if ($LASTEXITCODE -ne 0) { throw 'Could not start the WPR GPU recording.' }

try {
    $previousFlip = $env:WYU_WP15_FLIP
    $env:WYU_WP15_FLIP = $FlipMode
    & pwsh -NoProfile -File $replay -Assembly $assemblyPath -OutputDirectory (Join-Path $output 'replays')
    $replayExit = $LASTEXITCODE
} finally {
    $env:WYU_WP15_FLIP = $previousFlip
    & wpr -stop $trace
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $trace -PathType Leaf)) {
        throw 'The WPR GPU recording could not be saved.'
    }
}
$columns = 'Pid','Sequence','Fence','GpuAnchor','QpcAnchor','GpuHz','QpcHz',
    'FrameBegin','ShadowBegin','ShadowEnd','TerrainBegin','TerrainEnd','FrameEnd'
$aligned = foreach ($file in Get-ChildItem (Join-Path $output 'replays') -Filter '*.log') {
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
        if (!$line.StartsWith('WP15 CAL ', [StringComparison]::Ordinal)) { continue }
        $row = $line.Substring(9) | ConvertFrom-Csv -Header $columns
        $result = [ordered]@{ Run = $file.BaseName; Pid = [int]$row.Pid; Sequence = [ulong]$row.Sequence; Fence = [ulong]$row.Fence }
        foreach ($marker in $columns[7..12]) {
            $delta = [decimal]$row.$marker - [decimal]$row.GpuAnchor
            $result["${marker}Qpc"] = [long]([decimal]$row.QpcAnchor + $delta * [decimal]$row.QpcHz / [decimal]$row.GpuHz)
        }
        [pscustomobject]$result
    }
}
if (!$aligned) { throw "Recording saved to $trace, but the replay produced no calibrated GPU timestamps." }
$aligned | Export-Csv -LiteralPath (Join-Path $output 'markers-qpc.csv')
if ($presentMonPath) {
    $presents = Join-Path $output 'presents.csv'
    & $presentMonPath --etl_file $trace --process_name dotnet.exe --qpc_time --v2_metrics --no_console_stats --output_file $presents
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $presents -PathType Leaf)) {
        throw "PresentMon could not decode $trace."
    }
    $clientPids = @($aligned | Select-Object -ExpandProperty Pid -Unique)
    Import-Csv -LiteralPath $presents | Where-Object { [int]$_.ProcessID -in $clientPids } |
        Group-Object PresentMode | Select-Object Name,Count | Format-Table
}
if ($FlipMode) { Set-Content -LiteralPath (Join-Path $output 'requested-flip-mode.txt') -Value $FlipMode }
Write-Host "[FERTIG] $trace"
exit $replayExit
