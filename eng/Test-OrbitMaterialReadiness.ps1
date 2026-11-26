param([string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$client = (Resolve-Path -LiteralPath $Assembly).Path
$root = (New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot ('../artifacts/orbit-residency-test-' + [guid]::NewGuid().ToString('N')))).FullName
$references = foreach ($name in 'Groundwork.Core','Groundwork.Rendering','Groundwork.Rendering.D3D12','Groundwork.Voxels','Groundwork.Platform.Windows') {
    $path = [Security.SecurityElement]::Escape((Join-Path (Split-Path -Parent $client) "$name.dll"))
    "<Reference Include=`"$name`"><HintPath>$path</HintPath></Reference>"
}
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
  <ItemGroup><Compile Include="Program.cs" />$($references -join "`n")</ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $root 'Readiness.csproj')
@'
using System.Reflection;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;

// Managed ownership-state regression only. Native copy fences, staging release
// and held-window invariance are verified by the integrated hardware smoke.
var assembly = Assembly.LoadFrom(args[0]);
var type = assembly.GetType("WorldsYetUnseen.Client.OrbitTerrainMaterials", true)!;
const BindingFlags members = BindingFlags.NonPublic | BindingFlags.Instance;
var materials = type.GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
var stream = (D3D12TextureHandoff)type.GetMethod("CreateStream", members)!.Invoke(materials, null)!;
var resident = type.GetMethod("IsResident", members)!;
var publish = type.GetMethod("Publish", members)!;
var targets = (D3D12TextureTarget[])type.GetField("_targets", members)!.GetValue(materials)!;
Require(targets.Length == 6, "The production manifest must supply all six texture targets.");
var view = new D3D12TextureView(Guid.NewGuid(), new(1, 1, PlanetLocalFrame.Create(default)), 1, 1);
var streamType = stream.GetType();
var take = streamType.GetMethods(members).Single(method => method.Name == "TryTake" && method.GetParameters().Length == 2);
var submitted = streamType.GetMethod("MarkCopySubmitted", members)!;
var install = streamType.GetMethod("TryInstall", members)!;
var release = streamType.GetMethod("Release", members)!;
try
{
    Require(!Ready(false) && !Ready(true), "An unpublished stream is not resident.");
    Schedule(false);
    for (var i = 0; i < 5; i++) Require(CompleteOne(), "Missing coarse transfer.");
    Require(!Ready(true) && !Ready(false), "Five of six maps must not satisfy readiness.");
    Require(CompleteOne(), "Missing sixth coarse transfer.");
    Require(Ready(true), "All six coarse residents must satisfy the orbital target.");
    Require(!Ready(false), "Coarse residents must not satisfy near fine-detail readiness.");
    for (var turn = 0; turn < 6 && !Ready(false); turn++)
    {
        Schedule(false);
        while (CompleteOne()) { }
    }
    Require(Ready(false), "All six promoted residents must satisfy near readiness.");
    Require(!Ready(true), "Fine residents must not be mistaken for the fixed coarse orbit cut.");
    var oldView = view;
    Require(view.View.Frame.TryRebase(new(64, 0, 0), out var rebased), "The test must cross an origin cell.");
    view = view with { View = new(1, 2, rebased), CoverageGeneration = 2 };
    Schedule(false);
    Require(!(bool)resident.Invoke(materials, [stream, oldView, false])!, "A superseded pre-rebase cut must not report ready.");
    Console.WriteLine("PASSED: actual six-map residency rejects missing targets, wrong mip tiers and stale rebase cuts. No GPU used.");
}
finally
{
    streamType.GetMethod("Close", members)!.Invoke(stream, null);
}

bool Ready(bool orbital) => (bool)resident.Invoke(materials, [stream, view, orbital])!;
void Schedule(bool orbital)
{
    var result = publish.Invoke(materials, [stream, view, orbital])!.ToString();
    Require(result is "Scheduled" or "Retry", "The production material publication unexpectedly stopped.");
}
bool CompleteOne()
{
    object?[] arguments = [view, null];
    if (!(bool)take.Invoke(stream, arguments)!) return false;
    var transfer = arguments[1]!;
    var ticket = (D3D12TextureTicket)transfer.GetType().GetProperty("Ticket")!.GetValue(transfer)!;
    Require((bool)submitted.Invoke(stream, [ticket, null])!, "Managed copy submission failed.");
    object?[] installed = [ticket, view, null];
    Require((bool)install.Invoke(stream, installed)!, "Managed resident installation failed.");
    var previous = (D3D12TextureTicket)installed[2]!;
    if (!previous.Equals(default(D3D12TextureTicket)))
        Require((bool)release.Invoke(stream, [previous])!, "Superseded managed resident did not release.");
    return true;
}
void Require(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
'@ | Set-Content -LiteralPath (Join-Path $root 'Program.cs')
& dotnet run --project (Join-Path $root 'Readiness.csproj') -c Release --no-launch-profile -- $client *> (Join-Path $root 'test.log')
if ($LASTEXITCODE -ne 0) { throw "Managed Orbit readiness regression failed. Evidence $root/test.log" }
Get-Content -LiteralPath (Join-Path $root 'test.log')
Write-Host "Evidence $root"
