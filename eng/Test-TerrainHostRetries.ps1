param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('hardware','warp')][string]$Adapter = 'hardware'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$exe = (Resolve-Path -LiteralPath $Executable).Path
if (!(Test-Path -LiteralPath (Join-Path (Split-Path $exe) 'coreclr.dll') -PathType Leaf)) {
    throw 'Select the self-contained WYU executable.'
}
$out = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $out) { throw 'Choose a new output directory.' }
New-Item -ItemType Directory -Path $out | Out-Null
$prior = $env:WYU_HOST_RETRY_SMOKE
try {
    $env:WYU_HOST_RETRY_SMOKE = '1'
    foreach ($phase in 'flight','abort') {
        $world = Join-Path $out $phase
        New-Item -ItemType Directory -Path $world | Out-Null
        New-Item -ItemType File -Path (Join-Path $world 'wyu-volume-smoke.fixture') | Out-Null
        $log = Join-Path $out "$phase.stdout"
        $errors = Join-Path $out "$phase.stderr"
        $options = @{
            FilePath = $exe
            ArgumentList = @('--volume-flight-smoke',$phase,('"' + $world + '"'),$Adapter)
            RedirectStandardOutput = $log
            RedirectStandardError = $errors
            Wait = $true
            PassThru = $true
        }
        $process = Start-Process @options
        try {
            $lines = Get-Content -LiteralPath $log
            if ($process.ExitCode -ne 0 -or $lines -cnotcontains 'WP11 HOST RETRY SETTLED terrain=0 readers=0 generations=0') {
                throw "WYU $phase failed (exit $($process.ExitCode)). See $log and $errors."
            }
            foreach ($path in 'Movement','FirstEdit','Recovery') {
                if (@($lines | Where-Object { $_ -cmatch "^WP11 HOST RETRY path=$path attempts=2 denied=1 tick=\d+$" }).Count -ne 1) {
                    throw "Missing $path retry evidence in $log."
                }
            }
            if ($phase -eq 'flight' -and !($lines -match '^WP11 LANDED generation=2 ')) {
                throw "Missing landed checkpoint in $log."
            }
            Write-Host "PASS: $phase, all three host retries and settled shutdown."
        } finally {
            $process.Dispose()
        }
    }
} finally {
    $env:WYU_HOST_RETRY_SMOKE = $prior
}
