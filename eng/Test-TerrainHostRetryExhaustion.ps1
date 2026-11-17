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
$previousMode = $env:WYU_HOST_RETRY_SMOKE
try {
    foreach ($path in 'Movement','FirstEdit','Recovery') {
        $env:WYU_HOST_RETRY_SMOKE = $path
        $attempts = if ($path -eq 'FirstEdit') { 2 } else { 3 }
        $world = Join-Path $out $path
        New-Item -ItemType Directory -Path $world | Out-Null
        New-Item -ItemType File -Path (Join-Path $world 'wyu-volume-smoke.fixture') | Out-Null
        $log = Join-Path $out "$path.stdout"
        $errors = Join-Path $out "$path.stderr"
        $proc = Start-Process -FilePath $exe -ArgumentList @('--volume-flight-smoke','abort',('"' + $world + '"'),$Adapter) `
            -RedirectStandardOutput $log -RedirectStandardError $errors -PassThru
        try {
            if (!$proc.WaitForExit(180000)) { throw "WYU $path timed out. See $log and $errors." }
            $lines = Get-Content -LiteralPath $log
            $evidence = "WP11 HOST RETRY EXHAUSTED path=$path attempts=$attempts denied=$attempts released=true"
            if ($proc.ExitCode -ne 0 -or $lines -cnotcontains $evidence -or
                $lines -cnotcontains 'WP11 HOST RETRY SETTLED terrain=0 readers=0 generations=0' -or
                $lines -cnotcontains 'PASS: WYU WP11 exploration, rebase and native shutdown.' -or
                (Get-Item -LiteralPath $errors).Length -ne 0) {
                throw "WYU $path failed (exit $($proc.ExitCode)). See $log and $errors."
            }
            if ($path -eq 'FirstEdit' -and !($lines -cmatch '^WP11 HOST RETRY EDIT RECOVERED revision=\d+$')) {
                throw "Missing unchanged edit revision and restored movement evidence in $log."
            }
            Write-Host "PASS: $path exhausted after $attempts denials and released resources."
        } finally {
            if (!$proc.HasExited) { $proc.Kill(); $proc.WaitForExit() }
            $proc.Dispose()
        }
    }
} finally {
    $env:WYU_HOST_RETRY_SMOKE = $previousMode
}
