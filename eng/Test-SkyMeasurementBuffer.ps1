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
var previewAbi = type.GetMethod("HasPreviewAbi", statics)!.CreateDelegate<Func<Type, bool>>();
Require(previewAbi(typeof(PreviewAbi)) && !previewAbi(typeof(MissingPreviewAbi)) &&
    !previewAbi(typeof(WrongPreviewAbi)) && !previewAbi(typeof(NonLiteralPreviewAbi)),
    "Twilight Preview accepted a missing, wrong or mutable ABI stamp.");
var desktopSmoke = client.GetType("WorldsYetUnseen.Client.DesktopSmoke", true)!;
var clean = desktopSmoke.GetMethod("HasCleanDebugSettlement", statics)!
    .CreateDelegate<Func<D3D12DebugMessages?, bool, int, int, bool>>();
var settled = new D3D12DebugMessages(1, 0, 0, 0, 0, 0);
var retained = settled with { RetainedSessionQueue = true, LiveChildObjects = 3 };
Require(clean(settled, false, 0, 0) && clean(retained with { LiveChildObjects = 1 }, true, 0, 0) &&
    clean(retained, true, 2, 2) && clean(retained with { LiveChildObjects = 6 }, true, 5, 5),
    "Clean shutdown, legacy queue or named Preview inventory was rejected.");
Require(!clean(retained, false, 2, 2) && !clean(retained with { RetainedSessionQueue = false }, true, 2, 2) &&
    !clean(retained with { LiveChildObjects = 4 }, true, 2, 2) && !clean(retained, true, 2, 0) &&
    !clean(retained with { LiveChildObjects = 4 }, true, 3, 2) && !clean(retained, true, -1, 2) &&
    !clean(retained with { LiveChildObjects = null }, true, 2, 2),
    "Preview settlement accepted an unclassified, disallowed or out-of-range object count.");
Require(!clean(retained with { Errors = 1 }, true, 2, 2) &&
    !clean(retained with { Corruption = 1 }, true, 2, 2) &&
    !clean(retained with { Discarded = 1 }, true, 2, 2) &&
    !clean(retained with { LiveObjectReportResult = -1 }, true, 2, 2),
    "Preview settlement accepted invalid native diagnostics.");
var readPreviewObjects = desktopSmoke.GetMethod("ReadRetainedPreviewObjects", statics)!
    .CreateDelegate<Func<D3D12DebugMessages, int>>();
if (typeof(D3D12DebugMessages).GetProperty("RetainedSessionPreviewObjects") is null)
{
    var rejected = false;
    try { _ = readPreviewObjects(settled); }
    catch (InvalidOperationException) { rejected = true; }
    Require(rejected, "Preview audit accepted a provider with no native classification.");
}
else
    Require(readPreviewObjects(settled) == 0, "A new debug report did not start with zero classified objects.");
Console.WriteLine("PASSED: classified preview settlement accepts only exact session objects and rejects invalid native reports.");
var renderer = (D3D12Renderer)RuntimeHelpers.GetUninitializedObject(typeof(D3D12Renderer));
GC.SuppressFinalize(renderer);
var observer = type.GetConstructor(instance, null, [typeof(D3D12Renderer)], null)!.Invoke([renderer]);
var initialize = type.GetMethod("Initialize", instance)!;
var priorLookup = Environment.GetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_PROTOTYPE");
var priorReuse = Environment.GetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_REUSE_RESOURCES");
try
{
    Environment.SetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_REUSE_RESOURCES", null);
    foreach (var requested in new[] { false, true })
    foreach (var device in new object[] { new DirectOnlyDevice(), new LookupDevice(false), new LookupDevice(true) })
    {
        Environment.SetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_PROTOTYPE", requested ? "1" : null);
        var candidate = type.GetConstructor(instance, null, [typeof(D3D12Renderer)], null)!.Invoke([renderer]);
        var shouldPass = device is LookupDevice lookup ? lookup.SkyLookupPrototypeEnabledForTests == requested : !requested;
        var passed = false;
        try { initialize.Invoke(candidate, [device]); passed = true; }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { }
        Require(passed == shouldPass, "Prototype request silently accepted a missing/disabled private engine capability.");
        Require((bool)type.GetField("_lookupPrototype", instance)!.GetValue(candidate)! == (requested && shouldPass),
            "Prototype owner evidence did not reflect verified initialization.");
    }
}
finally
{
    Environment.SetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_PROTOTYPE", priorLookup);
    Environment.SetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_REUSE_RESOURCES", priorReuse);
}
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
var lookupGpu = type.GetMethod("LookupGpuTiming", instance)!.CreateDelegate<Action<ulong, ulong, double, double>>(observer);
var lookupRecord = type.GetMethod("LookupRecordTiming", instance)!.CreateDelegate<Action<ulong, ulong, double, double>>(observer);
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

    for (var i = 0; i < 64; i++)
    {
        lookupGpu(42, 1234, 7.125, 11.25);
        lookupRecord(42, 1234, 1.75, 2.125);
    }
    flush();
    output.GetStringBuilder().Clear();
    allocated = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < 1000; i++)
    {
        lookupGpu((ulong)i + 1, (ulong)i + 100, 7.125, 11.25);
        lookupRecord((ulong)i + 1, (ulong)i + 100, 1.75, 2.125);
    }
    Require(GC.GetAllocatedBytesForCurrentThread() == allocated && Count() == 2000 && output.GetStringBuilder().Length == 0,
        "Lookup timing callbacks allocated, wrote output or lost samples.");
    flush();
    var lookupRows = ReadRows(output);
    Require(lookupRows.Length == 2000, "Lookup split events were lost.");
    for (var i = 0; i < lookupRows.Length; i++)
    {
        var row = lookupRows[i];
        var gpu = i % 2 == 0;
        Require(row.GetProperty("kind").GetString() == (gpu ? "lookup-gpu-split" : "lookup-record-split") &&
            row.GetProperty("owner").GetInt32() == owner && row.GetProperty("sequence").GetUInt64() == (ulong)(i / 2 + 1) &&
            row.GetProperty("fence").GetUInt64() == (ulong)(i / 2 + 100) &&
            row.GetProperty("computeMicroseconds").GetDouble() == (gpu ? 7.125 : 1.75) &&
            row.GetProperty("drawBarrierMicroseconds").GetDouble() == (gpu ? 11.25 : 2.125),
            "Lookup split values or native identity changed.");
    }
    RequireDrained();
    output.GetStringBuilder().Clear();

    // A new renderer's first lookup execution must survive without a phase,
    // as occurs at both handoffs. Do not collect unbounded outside-window rows.
    var startupObserver = type.GetConstructor(instance, null, [typeof(D3D12Renderer)], null)!.Invoke([renderer]);
    var startupGpu = type.GetMethod("LookupGpuTiming", instance)!.CreateDelegate<Action<ulong, ulong, double, double>>(startupObserver);
    var startupRecord = type.GetMethod("LookupRecordTiming", instance)!.CreateDelegate<Action<ulong, ulong, double, double>>(startupObserver);
    var startupStage = type.GetMethod("LookupPreparationStage", instance)!.CreateDelegate<Action<string, long, long, bool>>(startupObserver);
    var startupFlush = type.GetMethod("FlushEvents", instance)!.CreateDelegate<Action>(startupObserver);
    var passTiming = type.GetMethod("PrepassTiming", instance)!
        .CreateDelegate<Action<ulong, ulong, double, double, double>>(startupObserver);
    var passDetails = type.GetMethod("GpuPassDetails", instance)!
        .CreateDelegate<Action<ulong, ulong, ulong, ulong, ulong, ulong>>(startupObserver);
    passTiming(42, 43, 31.5, 0, 2.25);
    passDetails(42, 43, 901, 915, 919, 937);
    Require((int)countField.GetValue(startupObserver)! == 0, "Unmeasured GPU pass entered the buffer.");
    for (ulong i = 1; i <= 20; i++)
    {
        startupGpu(900 + i, i, 1, 2);
        startupRecord(900 + i, i, 3, 4);
    }
    startupStage("textures", 10, 20, false);
    startupStage("descriptors", 20, 30, false);
    startupStage("compute-pipeline", 30, 40, true);
    startupStage("ready", 10, 40, true);
    Require(output.GetStringBuilder().Length == 0 && (int)countField.GetValue(startupObserver)! == 6,
        "Startup diagnostics printed or collected repeated outside-window work.");
    startupFlush();
    var startupRows = ReadRows(output);
    Require(startupRows.Length == 6 && startupRows[0].GetProperty("sequence").GetUInt64() == 901 &&
        startupRows[1].GetProperty("fence").GetUInt64() == 1 &&
        startupRows[0].GetProperty("phase").GetString() == "startup" &&
        startupRows[5].GetProperty("stage").GetString() == "ready" &&
        startupRows[5].GetProperty("start").GetInt64() == 10 &&
        startupRows[5].GetProperty("inclusiveOwnerPreparation").GetBoolean() &&
        startupRows[5].GetProperty("frequency").GetInt64() == Stopwatch.Frequency,
        "First-execution identity or inclusive startup interval was corrupted.");
    foreach (var invalidStage in new[] { "unknown", "textures", "descriptors", "resources-reused" })
    {
        var rejected = false;
        try { startupStage(invalidStage, 10, 20, true); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "Invalid stage or mutable-resource reuse was accepted.");
    }
    try
    {
        Environment.SetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_PROTOTYPE", "1");
        Environment.SetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_REUSE_RESOURCES", "1");
        foreach (var device in new object[] { new LookupDevice(true), new ReuseDevice(false) })
        {
            var rejected = false;
            try { initialize.Invoke(startupObserver, [device]); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { rejected = true; }
            Require(rejected, "A missing or disabled reuse capability was accepted.");
        }
        initialize.Invoke(startupObserver, [new ReuseDevice(true)]);
    }
    finally
    {
        Environment.SetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_PROTOTYPE", priorLookup);
        Environment.SetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_REUSE_RESOURCES", priorReuse);
    }
    output.GetStringBuilder().Clear();
    startupStage("resources-reused", 40, 41, true);
    startupFlush();
    var reuseRow = ReadRows(output).Single();
    Require(reuseRow.GetProperty("stage").GetString() == "resources-reused" &&
        reuseRow.GetProperty("reused").GetBoolean(), "Actual resource reuse was not retained in diagnostics.");
    type.GetField("_window", instance)!.SetValue(startupObserver, window);
    output.GetStringBuilder().Clear();
    passTiming(42, 43, 31.5, 0, 2.25);
    startupFlush();
    var passRow = ReadRows(output).Single();
    Require(passRow.GetProperty("kind").GetString() == "gpu-prepass" &&
        passRow.GetProperty("sequence").GetUInt64() == 42 && passRow.GetProperty("fence").GetUInt64() == 43 &&
        passRow.GetProperty("terrainMicroseconds").GetDouble() == 31.5 &&
        passRow.GetProperty("nearShadowMicroseconds").GetDouble() == 0 &&
        passRow.GetProperty("farShadowMicroseconds").GetDouble() == 2.25, "GPU pass counts or identity changed.");
    output.GetStringBuilder().Clear();
    passDetails(42, 43, 901, 915, 919, 937);
    Require(output.GetStringBuilder().Length == 0, "Pass tick callback wrote output before flush.");
    startupFlush();
    var passTicks = ReadRows(output).Single();
    Require(passTicks.GetProperty("kind").GetString() == "gpu-pass-ticks" &&
        passTicks.GetProperty("sequence").GetUInt64() == 42 && passTicks.GetProperty("fence").GetUInt64() == 43 &&
        passTicks.GetProperty("terrainStart").GetUInt64() == 901 && passTicks.GetProperty("terrainEnd").GetUInt64() == 915 &&
        passTicks.GetProperty("drawStart").GetUInt64() == 919 && passTicks.GetProperty("drawEnd").GetUInt64() == 937,
        "Pass tick endpoints or their native execution identity changed.");
    output.GetStringBuilder().Clear();
    startupGpu(42, 43, 5, 6);
    startupRecord(42, 43, 7, 8);
    startupFlush();
    Require(ReadRows(output).Length == 2, "Startup collection suppressed later measured samples.");
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

    // Metadata and startup clears must not add console work to owner startup
    // or the measured handoff. Handoff-only flights have no phase-finally.
    var eventType = type.GetNestedType("MeasurementEvent", BindingFlags.NonPublic)!;
    var eventKind = type.GetNestedType("EventKind", BindingFlags.NonPublic)!;
    var eventConstructor = eventType.GetConstructors(BindingFlags.Public | instance).Single(c => c.GetParameters().Length > 5);
    var eventParameters = eventConstructor.GetParameters();
    foreach (var name in new[] { "Owner", "UnboundPresent" })
    {
        var values = eventParameters.Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        values[0] = Enum.Parse(eventKind, name); values[1] = "startup";
        values[2] = 0UL; values[3] = 567UL; values[4] = 123456L;
        type.GetMethod("Buffer", instance)!.Invoke(observer, [eventConstructor.Invoke(values)]);
    }
    Require(output.GetStringBuilder().Length == 0 && Count() == 2, "Startup events performed console I/O.");
    type.GetMethod("RegisterEvidenceOwner", statics)!.Invoke(null, [observer]);
    type.GetMethod("FlushBeforeHandoff", statics)!.Invoke(null, null);
    rows = ReadRows(output);
    Require(rows.Length == 2 && rows[0].GetProperty("kind").GetString() == "owner" &&
        rows[1].GetProperty("kind").GetString() == "unbound-present" &&
        rows[1].GetProperty("fence").GetUInt64() == 567 && rows[1].GetProperty("timestamp").GetInt64() == 123456,
        "Pre-handoff export lost owner metadata or the unbound native Present.");
    RequireDrained(); output.GetStringBuilder().Clear();

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
        Require(kind is "record" or "gpu" or "prepare" or "owner" or "unbound-present" or "lookup-gpu-split" or "lookup-record-split" or "lookup-startup" or "gpu-prepass" or "gpu-pass-ticks", "Unexpected event kind in the native measurement stream.");
        return json.RootElement.Clone();
    }).ToArray();
static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
sealed class PreviewAbi { internal const int TwilightPreviewAbiVersion = 1; }
sealed class MissingPreviewAbi { }
sealed class WrongPreviewAbi { internal const int TwilightPreviewAbiVersion = 2; }
sealed class NonLiteralPreviewAbi { internal static int TwilightPreviewAbiVersion = 1; }
sealed class DirectOnlyDevice
{
    internal void EnableSkyTimingForTests() { }
}
sealed class LookupDevice(bool enabled)
{
    internal bool SkyLookupPrototypeEnabledForTests => enabled;
    internal void EnableSkyTimingForTests() { }
}
sealed class ReuseDevice(bool enabled)
{
    internal bool SkyLookupPrototypeEnabledForTests => true;
    internal bool SkyLookupResourceReuseEnabledForTests => enabled;
    internal void EnableSkyTimingForTests() { }
}
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
