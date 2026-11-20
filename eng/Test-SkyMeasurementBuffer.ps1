param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot ('../artifacts/sky-buffer-test-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$client = (Resolve-Path -LiteralPath $Assembly).Path
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh, exclusively owned buffer-test directory.' }
$root = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$clientHash = (Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash
$references = foreach ($file in Get-ChildItem -LiteralPath (Split-Path -Parent $client) -Filter '*.dll') {
    $path = [Security.SecurityElement]::Escape($file.FullName)
    "<Reference Include=`"$($file.BaseName)`"><HintPath>$path</HintPath></Reference>"
}
@"
<Project>
  <PropertyGroup><ImportDirectoryBuildProps>false</ImportDirectoryBuildProps><ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets></PropertyGroup>
  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
  <PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup>
  <ItemGroup><Compile Include="Program.cs" />$($references -join "`n")</ItemGroup>
  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
</Project>
"@ | Set-Content -LiteralPath (Join-Path $root 'Buffer.csproj')
@'
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Groundwork.Rendering.D3D12;

// Managed event storage only: no renderer constructor, initialization, disposal,
// device, native window, query heap or GPU method is called by this regression.
var client = Assembly.LoadFrom(args[0]);
var type = client.GetType("WorldsYetUnseen.Client.SkyMeasurements", true)!;
const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
var renderer = (D3D12Renderer)RuntimeHelpers.GetUninitializedObject(typeof(D3D12Renderer));
GC.SuppressFinalize(renderer);
var observer = type.GetConstructor(instance, null, [typeof(D3D12Renderer)], null)!.Invoke([renderer]);
var owners = type.GetField("Owners", statics)!.GetValue(null)!;
owners.GetType().GetMethod("Add")!.Invoke(owners, [renderer, observer]);
var windowType = type.GetNestedType("MeasurementWindow", BindingFlags.NonPublic)!;
var window = windowType.GetConstructor(BindingFlags.Public | instance, null,
    [typeof(string), typeof(ulong), typeof(ulong)], null)!.Invoke(["managed-phase", 1UL, 100000UL]);
type.GetField("_window", instance)!.SetValue(observer, window);
var owner = (int)type.GetField("_owner", instance)!.GetValue(observer)!;
var countField = type.GetField("_eventCount", instance)!;
var events = (Array)type.GetField("_events", instance)!.GetValue(observer)!;
var retired = type.GetMethod("Retired", instance)!.CreateDelegate<Action<ulong, ulong, double, double, bool>>(observer);
var recorded = type.GetMethod("Recorded", instance)!.CreateDelegate<Action<ulong, ulong, double, long, int>>(observer);
var prepared = type.GetMethod("Prepared", statics)!.CreateDelegate<Action<D3D12Renderer, ulong, double, long>>();
var flush = type.GetMethod("FlushEvents", instance)!.CreateDelegate<Action>(observer);
var outputConsole = Console.Out;
using var output = new StringWriter();
Console.SetOut(output);
try
{
    Require(events.Length == 8192, "The bounded measurement capacity changed.");
    // Warm all actual callback paths and the existing conditional weak table.
    for (var i = 0; i < 64; i++) AppendThree(42, 1234);
    Require(output.GetStringBuilder().Length == 0, "Warmup callbacks performed Console output.");
    flush();
    output.GetStringBuilder().Clear();
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < 1000; i++) AppendThree((ulong)i + 1, (ulong)i + 100);
    var appendBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
    Require(appendBytes == 0, $"Warmed callback appends allocated {appendBytes} bytes.");
    Require(output.GetStringBuilder().Length == 0 && Count() == 3000, "Appends wrote output or lost events.");
    flush();
    Require(ReadRows(output).Length == 3000, "Warmed appends were lost during drain.");
    RequireDrained();
    output.GetStringBuilder().Clear();

    var firstQpc = Stopwatch.GetTimestamp();
    AppendThree(42, 1234);
    var lastQpc = Stopwatch.GetTimestamp();
    Require(output.GetStringBuilder().Length == 0 && Count() == 3, "Measured callbacks wrote output before flush.");
    flush();
    var rows = ReadRows(output);
    Require(rows.Length == 3, "A flush must retain every event exactly once.");
    Require(rows[0].GetProperty("kind").GetString() == "record" && rows[1].GetProperty("kind").GetString() == "gpu" &&
        rows[2].GetProperty("kind").GetString() == "prepare", "Event kinds/order changed.");
    long previous = firstQpc;
    foreach (var row in rows)
    {
        Require(row.GetProperty("owner").GetInt32() == owner && row.GetProperty("sequence").GetUInt64() == 42 &&
            row.GetProperty("phase").GetString() == "managed-phase", "Event identity was corrupted.");
        var qpc = row.GetProperty("observedQpc").GetInt64();
        Require(qpc >= previous && qpc <= lastQpc, "QPC must describe append time, preserving event order.");
        previous = qpc;
    }
    Require(rows[0].GetProperty("fence").GetUInt64() == 1234 && rows[1].GetProperty("fence").GetUInt64() == 1234,
        "Native fence identity was lost.");
    Require(rows[0].GetProperty("recordMicroseconds").GetDouble() == 7.125 &&
        rows[0].GetProperty("allocatedBytes").GetInt64() == 37 && rows[0].GetProperty("draws").GetInt32() == 1,
        "Recording CPU/allocation/draw values changed.");
    Require(rows[1].GetProperty("frameMicroseconds").GetDouble() == 125.5 &&
        rows[1].GetProperty("skyMicroseconds").GetDouble() == 41.25 && rows[1].GetProperty("hasSky").GetBoolean(),
        "Retired GPU values changed.");
    Require(rows[2].GetProperty("preparationMicroseconds").GetDouble() == 3.5 &&
        rows[2].GetProperty("allocatedBytes").GetInt64() == 19, "Preparation values changed.");
    RequireDrained();
    output.GetStringBuilder().Clear();
    flush();
    Require(output.GetStringBuilder().Length == 0, "An empty drain repeated old events.");

    for (ulong i = 1; i <= 8192; i++) recorded(i, 20000 + i, i * 0.5, (long)i, 1);
    var overflow = false;
    try { recorded(8193, 28193, 999, 999, 1); }
    catch (InvalidOperationException error) { overflow = error.Message.Contains("capacity", StringComparison.OrdinalIgnoreCase); }
    Require(overflow && Count() == 8192 && output.GetStringBuilder().Length == 0,
        "Overflow must fail closed without overwriting, growing or printing the event buffer.");
    flush();
    rows = ReadRows(output);
    Require(rows.Length == 8192 && rows[0].GetProperty("sequence").GetUInt64() == 1 &&
        rows[0].GetProperty("fence").GetUInt64() == 20001 && rows[^1].GetProperty("sequence").GetUInt64() == 8192 &&
        rows[^1].GetProperty("fence").GetUInt64() == 28192 && rows[^1].GetProperty("recordMicroseconds").GetDouble() == 4096,
        "Overflow changed the retained prefix or its last accepted event.");
    RequireDrained();
    output.GetStringBuilder().Clear();

    AppendThree(42, 1234);
    using var failedPrefix = new StringWriter();
    using var failure = new PartialFailWriter(failedPrefix);
    Console.SetOut(failure);
    var failed = false;
    try { flush(); } catch (IOException error) { failed = error.Message == "measurement-sink-sentinel"; }
    finally { Console.SetOut(output); }
    Require(failed && Count() == 3 && ReadRows(failedPrefix).Length == 1,
        "A partial flush failure lost the full buffered prefix or hid the sink failure.");
    flush();
    var retried = ReadRows(output);
    Require(retried.Length == 3 && retried[0].GetRawText() == ReadRows(failedPrefix)[0].GetRawText() &&
        retried[1].GetProperty("fence").GetUInt64() == 1234 && retried[2].GetProperty("kind").GetString() == "prepare",
        "Retry did not retain the complete original buffer. Partial-output duplicates must remain detectable, not silently dropped.");
    RequireDrained();
    output.GetStringBuilder().Clear();
    recorded(99, 9999, 8, 0, 1);
    flush();
    rows = ReadRows(output);
    Require(rows.Length == 1 && rows[0].GetProperty("sequence").GetUInt64() == 99, "The drained buffer could not be reused cleanly.");
    RequireDrained();
}
finally { Console.SetOut(outputConsole); }
Console.WriteLine("PASSED: actual compiled SkyMeasurements callbacks retain exact values/fences/QPC, append with zero warmed allocations/output, fail closed at 8192, preserve partial-flush evidence and reset/drain. No GPU used.");

void AppendThree(ulong sequence, ulong fence)
{
    recorded(sequence, fence, 7.125, 37, 1);
    retired(sequence, fence, 125.5, 41.25, true);
    prepared(renderer, sequence, 3.5, 19);
}
int Count() => (int)countField.GetValue(observer)!;
void RequireDrained()
{
    Require(Count() == 0, "Successful drain did not reset the event count.");
    var first = events.GetValue(0)!;
    var last = events.GetValue(events.Length - 1)!;
    Require(first.GetType().GetProperty("Phase")!.GetValue(first) is null &&
        last.GetType().GetProperty("Phase")!.GetValue(last) is null,
        "Drained slots retained old event references.");
}
static JsonElement[] ReadRows(StringWriter writer) => writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
    .Select(line =>
    {
        const string prefix = "PACKAGE G MEASURE ";
        Require(line.StartsWith(prefix, StringComparison.Ordinal), "Unexpected measurement event prefix.");
        using var json = JsonDocument.Parse(line[prefix.Length..]);
        var kind = json.RootElement.GetProperty("kind").GetString();
        Require(kind is "record" or "gpu" or "prepare", "Unexpected event kind in the native measurement stream.");
        return json.RootElement.Clone();
    }).ToArray();
static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
sealed class PartialFailWriter(StringWriter prefix) : TextWriter
{
    private int count;
    public override Encoding Encoding => Encoding.UTF8;
    public override void WriteLine(string? value)
    {
        if (count++ != 0) throw new IOException("measurement-sink-sentinel");
        prefix.WriteLine(value);
    }
}
'@ | Set-Content -LiteralPath (Join-Path $root 'Program.cs')
$exitCode = 1
$verificationSucceeded = $false
$verificationFailure = $null
try {
    & dotnet run --project (Join-Path $root 'Buffer.csproj') -c Release --no-launch-profile -- $client *> (Join-Path $root 'test.log')
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "Managed measurement buffer regression failed. See $root/test.log" }
    if ((Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash -cne $clientHash) { throw 'Client assembly changed during the managed test.' }
    Get-Content -LiteralPath (Join-Path $root 'test.log')
    $verificationSucceeded = $true
}
catch { $verificationFailure = $_.Exception.Message; throw }
finally {
    [pscustomobject]@{
        Status=$(if ($verificationSucceeded) { 'PASSED' } else { 'CHANGES REQUIRED' })
        VerificationSucceeded=$verificationSucceeded; Failure=$verificationFailure
        Client=$client; ClientSha256=$clientHash; TestSourceSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
        GeneratedSourceSha256=(Get-FileHash -LiteralPath (Join-Path $root 'Program.cs') -Algorithm SHA256).Hash
        NoGpu=$true; ExitCode=$(if ($verificationSucceeded) { 0 } else { 1 }); DotnetExitCode=$exitCode
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'result.json')
}
Write-Host "Evidence $root"
