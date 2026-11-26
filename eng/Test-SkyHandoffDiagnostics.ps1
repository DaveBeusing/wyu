param([string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$client = (Resolve-Path -LiteralPath $Assembly).Path
$root = (New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot ('../artifacts/sky-handoff-test-' + [guid]::NewGuid().ToString('N')))).FullName
$references = foreach ($file in Get-ChildItem -LiteralPath (Split-Path -Parent $client) -Filter '*.dll') {
    $path = [Security.SecurityElement]::Escape($file.FullName)
    "<Reference Include=`"$($file.BaseName)`"><HintPath>$path</HintPath></Reference>"
}
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
  <ItemGroup><Compile Include="Program.cs" />$($references -join "`n")</ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $root 'Diagnostics.csproj')
@'
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

var enabled = args[1] != "off";
Environment.SetEnvironmentVariable("GROUNDWORK_TWILIGHT_PREVIEW", enabled ? "1" : null);
Environment.SetEnvironmentVariable("WYU_SKY_DIAGNOSTICS", "1");
var type = Assembly.LoadFrom(args[0]).GetType("WorldsYetUnseen.Client.SkyHandoffDiagnostics", true)!;
const BindingFlags methods = BindingFlags.NonPublic | BindingFlags.Static;
var prepare = type.GetMethod("Prepare", methods)!.CreateDelegate<Action>();
var begin = type.GetMethod("Begin", methods)!.CreateDelegate<Action<string, long>>();
var mark = type.GetMethod("Mark", methods)!.CreateDelegate<Action<string, ulong>>();
var complete = type.GetMethod("Complete", methods)!.CreateDelegate<Action<int, ulong, ulong, long>>();
var flushIncomplete = type.GetMethod("FlushIncomplete", methods)!.CreateDelegate<Action>();
var console = Console.Out;
using var output = new StringWriter();
Console.SetOut(output);
try
{
    prepare();
    mark("inactive", 0);
    if (args[1] == "exit")
    {
        begin("exit-without-present", Stopwatch.GetTimestamp());
        mark("owner-create-begin", 0);
        Console.SetOut(console);
        Console.WriteLine("PASSED: exiting with an incomplete diagnostic handoff.");
        return;
    }
    var start = Stopwatch.GetTimestamp();
    begin("to-orbit", start);
    string[] expectedStages = ["previous-stop-begin", "previous-props-stopped", "previous-renderer-dispose-begin",
        "previous-renderer-disposed", "owner-create-end", "far-frame-submit"];
    var allocatedBeforeStages = GC.GetAllocatedBytesForCurrentThread();
    foreach (var stage in expectedStages)
        mark(stage, stage == "far-frame-submit" ? 17UL : 0);
    var stageBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeStages;
    var end = Stopwatch.GetTimestamp();
    Require(output.GetStringBuilder().Length == 0, "Stage logging wrote output inside the native acceptance interval.");
    Require(stageBytes == 0, "Stage logging allocated after preparation.");
    complete(2, 17, 99, end);
    var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Require(lines.Length == (enabled ? expectedStages.Length : 0), "Stage count does not match the opt-in mode.");
    long previous = start;
    for (var index = 0; index < lines.Length; index++)
    {
        var line = lines[index];
        Require(line.StartsWith("PACKAGE G MEASURE ", StringComparison.Ordinal), "Unexpected stage output format.");
        using var json = JsonDocument.Parse(line["PACKAGE G MEASURE ".Length..]);
        var row = json.RootElement;
        Require(row.GetProperty("stage").GetString() == expectedStages[index], "Diagnostic stage order changed.");
        var timestamp = row.GetProperty("timestamp").GetInt64();
        Require(timestamp >= previous && timestamp <= end, "Stage ordering or native endpoint correlation is invalid.");
        Require(row.GetProperty("start").GetInt64() == start && row.GetProperty("end").GetInt64() == end,
            "The diagnostic changed the acceptance endpoints.");
        Require(row.GetProperty("owner").GetInt32() == 2 && row.GetProperty("frameSequence").GetUInt64() == 17 &&
            row.GetProperty("presentFence").GetUInt64() == 99, "Native identity was lost.");
        previous = timestamp;
    }
    if (enabled)
    {
        begin("to-ground", Stopwatch.GetTimestamp());
        for (var i = 0; i < 64; i++) mark("capacity", (ulong)i);
        var overflow = false;
        try { mark("overflow", 65); } catch (InvalidOperationException error) { overflow = error.Message.Contains("capacity"); }
        Require(overflow, "Diagnostic overflow silently dropped a stage.");
        complete(3, 18, 101, Stopwatch.GetTimestamp());
        output.GetStringBuilder().Clear();
        begin("failure", Stopwatch.GetTimestamp());
        mark("renderer-start-begin", 0);
        flushIncomplete();
        flushIncomplete();
        var failedLines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Require(failedLines.Length == 1, "Incomplete diagnostic evidence was duplicated or lost.");
        using var failed = JsonDocument.Parse(failedLines[0]["PACKAGE G MEASURE ".Length..]);
        Require(failed.RootElement.GetProperty("status").GetString() == "INCOMPLETE" &&
            failed.RootElement.GetProperty("end").ValueKind == JsonValueKind.Null &&
            failed.RootElement.GetProperty("finalDiagnosticOnly").GetBoolean(), "Incomplete stages incorrectly claim a native endpoint.");
        var errorConsole = Console.Error;
        using var broken = new FailingWriter();
        try
        {
            begin("failed-sink", Stopwatch.GetTimestamp());
            mark("previous-stop-begin", 0);
            Console.SetOut(broken);
            Console.SetError(broken);
            flushIncomplete();
        }
        finally { Console.SetOut(output); Console.SetError(errorConsole); }
    }
}
finally { Console.SetOut(console); }
Console.WriteLine($"PASSED: {args[1]} stage buffering, zero stage allocations/output, ordering and native identity" +
    (enabled ? ", fatal overflow." : "."));
void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
sealed class FailingWriter : TextWriter
{
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    public override void WriteLine(string? value) => throw new IOException("diagnostic sink unavailable");
}
'@ | Set-Content -LiteralPath (Join-Path $root 'Program.cs')
& dotnet build (Join-Path $root 'Diagnostics.csproj') -c Release *> (Join-Path $root 'build.log')
if ($LASTEXITCODE -ne 0) { throw "Managed handoff diagnostics test build failed. Evidence $root/build.log" }
foreach ($mode in 'off','on','exit') {
    & dotnet run --project (Join-Path $root 'Diagnostics.csproj') -c Release --no-build -- $client $mode *> (Join-Path $root "$mode.log")
    if ($LASTEXITCODE -ne 0) { throw "Managed handoff diagnostics test $mode failed. Evidence $root/$mode.log" }
    Get-Content -LiteralPath (Join-Path $root "$mode.log")
    if ($mode -eq 'exit' -and -not (Select-String -LiteralPath (Join-Path $root "$mode.log") -Pattern '"status":"INCOMPLETE".*"end":null')) {
        throw 'Process exit did not retain the incomplete diagnostic stages.'
    }
}
Write-Host "Evidence $root"
