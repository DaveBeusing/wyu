# Verify the WYU project graph and build from the supplied local feed.
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
function Assert-True([bool] $condition, [string] $message) {
    if (-not $condition) { throw $message }
}
function Invoke-Native([string] $exe, [string[]] $arguments) {
    Write-Host ('> ' + $exe + ' ' + ($arguments -join ' '))
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) {
        throw ("{0} failed with exit code {1}" -f $exe, $LASTEXITCODE)
    }
}
Push-Location $root
try {
    Assert-True (Test-Path 'WorldsYetUnseen.slnx') 'WYU solution is missing.'
    [xml] $solution = Get-Content 'WorldsYetUnseen.slnx' -Raw
    $projects = @($solution.SelectNodes('//Project') | ForEach-Object { $_.GetAttribute('Path') })
    $clientProject = 'src/WorldsYetUnseen.Client/WorldsYetUnseen.Client.csproj'
    $gameplayProject = 'src/WorldsYetUnseen.Gameplay/WorldsYetUnseen.Gameplay.csproj'
    $gameplayTests = 'tests/WorldsYetUnseen.Gameplay.Tests/WorldsYetUnseen.Gameplay.Tests.csproj'
    Assert-True ($projects.Count -eq 3 -and $projects -ccontains $clientProject -and
        $projects -ccontains $gameplayProject -and $projects -ccontains $gameplayTests) 'Unexpected WYU project graph.'
    [xml] $props = Get-Content 'Directory.Build.props' -Raw
    $version = $props.SelectSingleNode('//PropertyGroup/Version')
    Assert-True (($null -ne $version) -and ($version.InnerText -eq '0.1.0')) 'Source version must be 0.1.0.'
    [xml] $project = Get-Content $clientProject -Raw
    $refs = @($project.SelectNodes('//ProjectReference'))
    Assert-True ($refs.Count -eq 1 -and
        $refs[0].GetAttribute('Include') -ceq '../WorldsYetUnseen.Gameplay/WorldsYetUnseen.Gameplay.csproj') 'Unexpected WYU host project dependency.'
    $packages = @($project.SelectNodes('//PackageReference'))
    $expectedVersion = '0.1.0-dev.42417550607.1'
    $expectedIds = @('Core', 'Diagnostics', 'Ecs', 'Jobs', 'Persistence', 'Platform.Windows', 'Rendering', 'Rendering.D3D12', 'Simulation', 'Voxels') |
        ForEach-Object { "Groundwork.$_" }
    Assert-True ($packages.Count -eq $expectedIds.Count) 'Unexpected WYU package reference count.'
    $lockPath = 'src/WorldsYetUnseen.Client/packages.lock.json'
    Assert-True (Test-Path $lockPath -PathType Leaf) 'Generated and committed NuGet lock file is missing.'
    $lock = Get-Content $lockPath -Raw | ConvertFrom-Json
    $framework = $lock.dependencies.PSObject.Properties | Where-Object { $_.Name -like 'net10.0-windows*' } | Select-Object -First 1
    Assert-True ($null -ne $framework) 'Unexpected lockfile target framework.'
    Assert-True ($framework.Value.PSObject.Properties.Name.Count -eq ($expectedIds.Count + 2)) 'Unexpected WYU lockfile dependency count.'
    $gameplayLock = $framework.Value.PSObject.Properties['worldsyetunseen.gameplay']
    Assert-True ($null -ne $gameplayLock -and $gameplayLock.Value.type -ceq 'Project') 'Missing local gameplay project in the lockfile.'
    foreach ($id in $expectedIds) {
        $reference = @($packages | Where-Object { $_.GetAttribute('Include') -ceq $id })
        Assert-True (($reference.Count -eq 1) -and ($reference[0].GetAttribute('Version') -ceq "[$expectedVersion]")) "Expected exactly pinned package $id."
        $locked = $framework.Value.PSObject.Properties[$id]
        Assert-True (($null -ne $locked) -and ($locked.Value.type -ceq 'Direct') -and
            ($locked.Value.resolved -ceq $expectedVersion) -and
            ($locked.Value.requested -ceq "[$expectedVersion, $expectedVersion]")) "Lockfile does not agree with exact package $id."
    }
    $binding = $framework.Value.PSObject.Properties['TerraFX.Interop.Windows']
    Assert-True (($null -ne $binding) -and ($binding.Value.type -ceq 'Transitive') -and
        ($binding.Value.resolved -ceq '10.0.26100.6')) 'Unexpected D3D12 binding in the lockfile.'
    [xml] $config = Get-Content 'NuGet.Config' -Raw
    $feed = $config.SelectSingleNode('//packageSources/add[@key="groundwork-validated-local"]')
    Assert-True (($null -ne $feed) -and ($feed.GetAttribute('value') -eq '%GROUNDWORK_NUGET_FEED%')) 'Validated feed environment mapping is missing.'
    $groundworkMapping = $config.SelectSingleNode('//packageSourceMapping/packageSource[@key="groundwork-validated-local"]/package[@pattern="Groundwork.*"]')
    Assert-True ($null -ne $groundworkMapping) 'Groundwork packages must map to the validated local feed.'
    $publicMapping = @($config.SelectNodes('//packageSourceMapping/packageSource[@key="nuget.org"]/package'))
    Assert-True (($publicMapping.Count -eq 1) -and
        ($publicMapping[0].GetAttribute('pattern') -ceq 'TerraFX.Interop.Windows')) 'Only the approved D3D12 binding may map to nuget.org.'
    Assert-True (-not [string]::IsNullOrWhiteSpace($env:GROUNDWORK_NUGET_FEED)) 'Set GROUNDWORK_NUGET_FEED to a validated local directory outside worktrees.'
    $feedPath = [System.IO.Path]::GetFullPath($env:GROUNDWORK_NUGET_FEED)
    $rootWithSeparator = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    Assert-True (-not $feedPath.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) 'The NuGet feed must be outside the WYU worktree.'
    Assert-True (Test-Path $feedPath -PathType Container) 'Validated feed directory does not exist.'
    $tracked = @(& git ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }
    $generated = @($tracked | Where-Object { $_ -match '(^|/)(bin|obj|artifacts|dist)/|\.(nupkg|snupkg|dll|exe)$' })
    Assert-True ($generated.Count -eq 0) ('Generated files are tracked: ' + ($generated -join ', '))
    Invoke-Native 'dotnet' @('--version')
    $lockHash = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash
    Invoke-Native 'dotnet' @('restore', 'WorldsYetUnseen.slnx', '--configfile', 'NuGet.Config', '--locked-mode', '--force')
    Assert-True ((Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash -eq $lockHash) 'Locked restore changed the NuGet lockfile.'
    Invoke-Native 'dotnet' @('build', 'WorldsYetUnseen.slnx', '-c', 'Release', '--no-restore')
    Write-Host 'PASS: WYU structure, exact NuGet pins, locked restore and Release build.' -ForegroundColor Green
    Invoke-Native 'dotnet' @('run', '--project', $gameplayTests, '-c', 'Release', '--no-build', '--no-restore')
} finally {
    Pop-Location
}
