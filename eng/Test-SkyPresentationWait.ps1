param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot ('../artifacts/sky-presentation-wait-test-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$client = (Resolve-Path -LiteralPath $Assembly).Path
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh, exclusively owned presentation-wait test directory.' }
$root = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$clientHash = (Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash
$sourceHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
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
"@ | Set-Content -LiteralPath (Join-Path $root 'Wait.csproj')
@'
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Groundwork.Rendering;

// Only managed snapshot values and supplied delegates are used. No renderer
// constructor, native window, D3D12 device or GPU operation is invoked.
var assembly = Assembly.LoadFrom(args[0]);
var type = assembly.GetType("WorldsYetUnseen.Client.SkyMeasurements", true)!;
const BindingFlags members = BindingFlags.Static | BindingFlags.NonPublic;
var helper = type.GetMethod("AwaitPresentationAsync", members) ??
    throw new InvalidOperationException("The supplied client does not contain the presentation wait helper.");
var awaitPresentation = helper.CreateDelegate<
    Func<Func<ulong, CancellationToken, ValueTask<RendererSnapshot>>, ulong, TimeSpan, CancellationToken, ValueTask<RendererSnapshot>>>();
const ulong requested = 73;
var bound = TimeSpan.FromSeconds(2);

using (var caller = new CancellationTokenSource())
{
    foreach (var presented in new[] { requested, requested + 5 })
    {
        var calls = 0;
        var result = await awaitPresentation((sequence, token) =>
        {
            calls++;
            Require(sequence == requested && token.CanBeCanceled && !token.IsCancellationRequested,
                "The wait did not receive the requested sequence and an active cancellable token.");
            return ValueTask.FromResult(Snapshot(presented));
        }, requested, bound, caller.Token);
        Require(calls == 1 && result.LastPresentedSequence == presented, "Immediate exact/superset acknowledgement failed or polled.");
    }

    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var acknowledged = new TaskCompletionSource<RendererSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    var delayedCalls = 0;
    var pending = awaitPresentation((sequence, token) =>
    {
        delayedCalls++;
        Require(sequence == requested && token.CanBeCanceled && !token.IsCancellationRequested,
            "Delayed wait did not receive the requested sequence and active cancellation semantics.");
        entered.SetResult();
        return new(acknowledged.Task);
    }, requested, bound, caller.Token);
    await entered.Task.WaitAsync(bound);
    Require(!pending.IsCompleted && delayedCalls == 1, "Wait completed before its acknowledgement or repeatedly invoked the source.");
    acknowledged.SetResult(Snapshot(requested));
    var delayed = await pending.AsTask().WaitAsync(bound);
    Require(delayed.LastPresentedSequence == requested && delayedCalls == 1, "TaskCompletionSource acknowledgement did not resume exactly one wait.");
}

await RejectSnapshot(Snapshot(requested - 1));
foreach (var lifecycle in Enum.GetValues<RendererLifecycle>().Where(value => value != RendererLifecycle.Running))
    await RejectSnapshot(Snapshot(requested, lifecycle));
await RejectSnapshot(Snapshot(requested) with { FirstFault = new RenderFaultSummary(1, -1, null, requested) });

using (var caller = new CancellationTokenSource())
{
    var calls = 0;
    var never = new TaskCompletionSource<RendererSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    CancellationToken underlyingToken = default;
    CancellationTokenRegistration registration = default;
    var pending = awaitPresentation((sequence, token) =>
    {
        calls++;
        Require(sequence == requested && token.CanBeCanceled, "The underlying wait requires a cancellable lifetime.");
        underlyingToken = token;
        registration = token.Register(() =>
        {
            never.TrySetCanceled(token);
            cancellationObserved.TrySetResult();
        });
        return new(never.Task);
    }, requested, bound, caller.Token);
    Require(!pending.IsCompleted && calls == 1, "Caller cancellation test did not enter one pending wait.");
    caller.Cancel();
    var cancelled = false;
    try { await pending.AsTask().WaitAsync(bound); }
    catch (OperationCanceledException) { cancelled = caller.IsCancellationRequested; }
    await cancellationObserved.Task.WaitAsync(bound);
    registration.Dispose();
    Require(cancelled && calls == 1 && underlyingToken.IsCancellationRequested && never.Task.IsCanceled,
        "Caller cancellation did not cancel the underlying wait exactly once.");
}
using (var caller = new CancellationTokenSource())
{
    caller.Cancel();
    var calls = 0;
    var cancelled = false;
    try
    {
        await awaitPresentation((_, _) => { calls++; return ValueTask.FromResult(Snapshot(requested)); },
            requested, bound, caller.Token);
    }
    catch (OperationCanceledException error) { cancelled = error.CancellationToken == caller.Token; }
    Require(cancelled && calls == 0, "A pre-cancelled caller must not invoke the renderer wait.");
}

foreach (var failCancellationCallback in new[] { false, true })
{
    var calls = 0;
    var never = new TaskCompletionSource<RendererSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    CancellationToken underlyingToken = default;
    CancellationTokenRegistration completionRegistration = default;
    CancellationTokenRegistration failingRegistration = default;
    var errorConsole = Console.Error;
    using var errors = new StringWriter();
    Console.SetError(errors);
    var started = Stopwatch.GetTimestamp();
    var timedOut = false;
    try
    {
        await awaitPresentation((_, token) =>
        {
            calls++;
            Require(token.CanBeCanceled, "Even a caller without cancellation needs a bounded underlying wait token.");
            underlyingToken = token;
            completionRegistration = token.Register(() =>
            {
                never.TrySetCanceled(token);
                cancellationObserved.TrySetResult();
            });
            if (failCancellationCallback)
                failingRegistration = token.Register(() => throw new IOException("cancellation-callback-sentinel"));
            return new(never.Task);
        }, requested, TimeSpan.FromMilliseconds(25), CancellationToken.None).AsTask().WaitAsync(bound);
    }
    catch (TimeoutException) { timedOut = true; }
    finally { Console.SetError(errorConsole); }
    await cancellationObserved.Task.WaitAsync(bound);
    completionRegistration.Dispose();
    failingRegistration.Dispose();
    Require(timedOut && calls == 1 && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1) &&
        underlyingToken.IsCancellationRequested && never.Task.IsCanceled,
        "The supplied short deadline did not cancel and finish its underlying wait while preserving TimeoutException.");
    Require(failCancellationCallback ? errors.ToString().Contains("cancellation-callback-sentinel", StringComparison.Ordinal) : errors.GetStringBuilder().Length == 0,
        "Cancellation callback failures must be diagnostic evidence and must not replace the original timeout.");
}

foreach (var asynchronous in new[] { false, true })
{
    var sentinel = new IOException("supplied-wait-sentinel");
    var calls = 0;
    Exception? observed = null;
    try
    {
        await awaitPresentation((_, _) =>
        {
            calls++;
            if (!asynchronous) throw sentinel;
            return ValueTask.FromException<RendererSnapshot>(sentinel);
        }, requested, bound, CancellationToken.None);
    }
    catch (IOException error) { observed = error; }
    Require(ReferenceEquals(observed, sentinel) && calls == 1, "Supplied synchronous/asynchronous wait failure was wrapped, hidden or retried.");
}

var stateMachine = helper.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType ??
    throw new InvalidOperationException("Expected the compiled asynchronous wait state machine.");
var moveNext = stateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
var helperInstructions = Instructions(moveNext).ToArray();
Require(!helperInstructions.Any(instruction => instruction.Operand is MethodBase method &&
    ((method.DeclaringType == typeof(Task) && method.Name == "Delay") ||
     (method.DeclaringType == typeof(Thread) && method.Name == "Sleep"))),
    "The compiled helper contains an arbitrary delay/sleep polling path.");
var wrapper = type.GetMethod("WaitForPresentedAsync", members)!;
var wrapperInstructions = Instructions(wrapper).ToArray();
Require(wrapperInstructions.Any(instruction => instruction.Operand is MethodBase method &&
    method.DeclaringType == typeof(TimeSpan) && method.Name == "FromSeconds") &&
    wrapperInstructions.Any(instruction => instruction.Operand is IConvertible number &&
        instruction.Operand is not MethodBase && Convert.ToDouble(number, System.Globalization.CultureInfo.InvariantCulture) == 30),
    "The production wrapper no longer contains its unchanged 30-second bound.");
Console.WriteLine("PASSED: actual compiled presentation helper invokes one cancellable wait, accepts exact/superset acknowledgements, resumes on TCS, rejects premature/stopped/faulted snapshots, cancels underlying waits on caller cancellation/timeout, preserves timeout despite cancellation callback errors, propagates source exceptions, and contains no delay polling. Production 30-second wrapper retained. No GPU used.");

RendererSnapshot Snapshot(ulong presented, RendererLifecycle lifecycle = RendererLifecycle.Running) =>
    new(lifecycle, RenderSurfaceState.Drawable, 1920, 1080, Math.Max(requested, presented), presented, 0, 0, null, null, false);
async Task RejectSnapshot(RendererSnapshot snapshot)
{
    var calls = 0;
    var rejected = false;
    try
    {
        await awaitPresentation((_, _) => { calls++; return ValueTask.FromResult(snapshot); },
            requested, bound, CancellationToken.None);
    }
    catch (InvalidOperationException) { rejected = true; }
    Require(rejected && calls == 1, "A premature, stopped or faulted snapshot was accepted or polled.");
}
static IEnumerable<(OpCode Code, object? Operand)> Instructions(MethodInfo method)
{
    var codes = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => unchecked((ushort)code.Value));
    var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
    for (var offset = 0; offset < bytes.Length;)
    {
        var value = (ushort)bytes[offset++];
        if (value == 0xfe) value = (ushort)(0xfe00 | bytes[offset++]);
        var code = codes[value];
        object? operand = null;
        var size = code.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
            _ => 4
        };
        if (code.OperandType == OperandType.InlineMethod) operand = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, offset));
        else if (code.OperandType == OperandType.ShortInlineI) operand = unchecked((sbyte)bytes[offset]);
        else if (code.OperandType == OperandType.InlineI) operand = BitConverter.ToInt32(bytes, offset);
        else if (code.OperandType == OperandType.InlineI8) operand = BitConverter.ToInt64(bytes, offset);
        else if (code.OperandType == OperandType.InlineR) operand = BitConverter.ToDouble(bytes, offset);
        else if (code.OperandType == OperandType.ShortInlineR) operand = BitConverter.ToSingle(bytes, offset);
        offset += size;
        yield return (code, operand);
    }
}
static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
'@ | Set-Content -LiteralPath (Join-Path $root 'Program.cs')
$dotnetExitCode = 1
$verificationSucceeded = $false
$verificationFailure = $null
try {
    & dotnet run --project (Join-Path $root 'Wait.csproj') -c Release --no-launch-profile -- $client *> (Join-Path $root 'test.log')
    $dotnetExitCode = $LASTEXITCODE
    if ($dotnetExitCode -ne 0) { throw "Managed presentation-wait regression failed. See $root/test.log" }
    if ((Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash -cne $clientHash) { throw 'Client assembly changed during the managed test.' }
    if ((Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash -cne $sourceHash) { throw 'Test source changed during the managed test.' }
    Get-Content -LiteralPath (Join-Path $root 'test.log')
    $verificationSucceeded = $true
}
catch { $verificationFailure = $_.Exception.Message; throw }
finally {
    [pscustomobject]@{
        Status=$(if ($verificationSucceeded) { 'PASSED' } else { 'CHANGES REQUIRED' })
        VerificationSucceeded=$verificationSucceeded; Failure=$verificationFailure
        Client=$client; ClientSha256=$clientHash; TestSourceSha256=$sourceHash
        GeneratedSourceSha256=(Get-FileHash -LiteralPath (Join-Path $root 'Program.cs') -Algorithm SHA256).Hash
        NoGpu=$true; ExitCode=$(if ($verificationSucceeded) { 0 } else { 1 }); DotnetExitCode=$dotnetExitCode
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'result.json')
}
Write-Host "Evidence $root"
