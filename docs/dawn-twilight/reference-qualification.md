# Reference qualification runbook

TW1–TW3 are approved. The local two-seed hardware flight/Continue image
qualification passed after the DPI capture correction. The owner will run
the performance evidence on the accepted i9-10850K / RTX 5070 reference system.
Results from the RTX PRO 5000 workstation are diagnostic evidence only.

The integrated candidate now includes Orbit Demo from WYU master and the complete
Groundwork #186 follow-up. Earlier evidence applies only to its recorded heads.
See [combined candidate acceptance](final-acceptance.md) for the integration
boundary, current package baseline and the required renewed checks.

For the outstanding Far Horizon per-pair failures, use the
[GPU/handoff investigation and focused diagnostic probe](far-horizon-diagnostics.md).
Diagnostic telemetry and reduced-flight probes never replace acceptance runs.

## Freeze the inputs

Use clean checkouts of the final reviewed heads of Groundwork PR #193 and
WYU PR #83. Record both full commit IDs before building. Keep the same OS,
driver, display topology, power state and presentation settings throughout
each comparison. Do not run the two measurement programs concurrently.

The tools require Windows x64, PowerShell 7, .NET SDK 10 and the hardware
D3D12 adapter. The integrated run also needs an interactive desktop on which
the test window can expose a native 1920 × 1080 drawable client. A smaller or
software-adapter smoke run cannot qualify these limits. Close other games,
captures and GPU benchmarks; record any unavoidable concurrent workload.

Use a new output directory for every attempt, including failed attempts.
Never delete a failed run to improve a percentile or reuse its directory.

## 1. Renderer pass fixture

From the clean Groundwork checkout:

```powershell
./eng/Measure-SkyQualification.ps1 -OutputDirectory D:\Evidence\sky-pass-reference-01
```

Do not specify `-LocalProbe`, `-AllowDirty` or `-SkipBuild` for reference
evidence. Strict mode uses 1920 × 1080, five alternating pairs, 2,000 retained
samples per variant/pair/phase and 64 warmups. It checks the unchanged sky
p95 ≤0.8 ms / p99 ≤1.2 ms limits, renderer preparation/recording p99 ≤0.10 ms,
zero warmed recording allocations and native sky draw counts.

This is a synthetic renderer fixture, not the real WYU profile or installed
route. Its radius/shell/scale-height inputs and all measurement scope are in
the output. Its CPU interval excludes application preparation. Tracked
resource backing excludes unobservable driver PSO/root-signature memory.
Do not promote this result into an integrated or complete memory result.

Preserve the entire output directory: raw CSV, summary JSON, TRX, console log,
hardware/source records and hashes, even when the command returns nonzero.

## 2. Real WYU profile and handoffs

Build WYU from its unchanged, approved immutable package pin using
`eng/Test-VerifiedGroundworkConsumer.ps1` and the exact archive/hash/version
listed in [build-bootstrap](../build-bootstrap.md). That verification must
pass before staging a diagnostic renderer. Do not change the NuGet pins,
lockfiles or master-only producer checks to accept a PR artifact.

For this explicitly identified diagnostic run, replace the output folder's
`Groundwork.Rendering.dll` and `Groundwork.Rendering.D3D12.dll` together with
their tested Release DLLs from the same clean Groundwork PR checkout. The #186
presentation-wait API spans both assemblies; a provider-only overlay omits part
of the integrated candidate. Record the full source commit and the client and
both rendering hashes immediately after the controlled build and
staging step. Retain those values independently of the runner output; compare
them with the build records before execution. This is not an approved archive
or a production WYU installation.

From the clean WYU checkout, with those recorded values:

```powershell
foreach ($composition in 'far-horizon', 'orbit-combined') {
  ./eng/Measure-ReferenceSky.ps1 `
    -Assembly $clientAssembly `
    -OutputDirectory "D:\Evidence\sky-$composition-reference-01" `
    -Adapter hardware `
    -Composition $composition `
    -ExpectedClientSha256 $recordedClientSha256 `
    -ExpectedRendererSha256 $recordedRendererSha256 `
    -ExpectedRenderingSha256 $recordedRenderingSha256 `
    -RendererSourceCommit $recordedRendererCommit
}
```

The variables above identify the controlled build; do not substitute values
from an unverified downloaded binary. The runner rejects mismatches. Do not
use `-Smoke` for reference qualification.

The software-only `-Smoke -Adapter warp -Composition orbit-combined` diagnostic
allows six minutes inside the child and 390 seconds in the parent. The complete
PBR/shadow route exceeded the older three-minute smoke deadline while continuing
to render. This allowance is not a performance acceptance limit. Hardware smoke
remains 180/210 seconds, and full hardware runs remain 600/660 seconds. Failed
earlier attempts are retained.

Desktop image fixtures capture the complete physical client without rescaling.
JSON separates native render dimensions from physical BMP dimensions. Horizon
handoff checks explicitly select the upper two-thirds of the physical client;
sun/stars and twilight captures use the full client. Earlier render-sized desktop
captures at non-unit DPI could be cropped and cannot establish full-frame visual
equivalence. Pixel/centroid tolerances remain unchanged.

Full mode runs five alternating off/on pairs with six fresh child flights
per pair and variant: 60 attempts total, at least 30 handoffs in each
direction per variant. The first child of each run supplies 400 retained
samples after 40 warmups in each surface/ascent/orbit phase, giving 2,000
samples per phase and variant. Each composition requires its own complete
60-attempt run, for 120 attempts total. Retain failures from both compositions.

Packages B/E and canonical G cameras stay enabled in both variants. Only the
submitted sky value is removed in the off variant, after camera and globe
construction, so the legacy off-camera/FOV cannot distort the comparison.
The script rejects mismatching phase/route identities and incomplete counts.
CPU/GPU rows are correlated by renderer owner and native draw fence, because
appearance updates can redraw an acknowledged logical sequence. Every native
execution remains recorded. Smoke may contain additional executions; strict
controlled windows reject them after preserving all raw rows. No first-row
selection or percentile exclusion hides a redraw.
Far Horizon retains its original WP15/props-off measurement. Orbit combined
uses its actual preset sun, PBR maps, boulders, sunlight/shadows and props in
both variants. Strict held phases reject texture uploads that did not settle
before the held window. Orbit first pumps the normal production material path
until all six current-cut maps have their requested mip and staging is empty.
This preparation is bounded to 208 acknowledged frames and the existing process
deadline; it fails closed if readiness is not established. Its frames and texture
state are recorded separately before the unchanged 40 warmups and sample window.
No preparation frame is substituted for a requested sample. The upload/staging
guard also applies to Orbit smoke runs. Canonical identities include sky,
sunlight and scene transforms.

The tool checks direct graphics-command-list GPU timestamps, sky cost,
preparation plus recording CPU/allocation/draw observations and exact successful-native-Present
handoffs. It retains per-run and pooled results and enforces unchanged total
GPU p95 ≤+5% / p99 ≤+8%, sky ≤0.8/1.2 ms and handoff p95 ≤+10% limits.
Every required per-pair row and pooled row must pass. The
graphics interval excludes separate uploads and native Present; phase geometry
is held fixed. Native `Present(1, 0)` configuration is not verified compositor
scanout/presentation-mode evidence.

The integrated CPU interval now sums synchronous production weather/sky/sunlight
preparation and owner sky recording for the exact owner/sequence. It excludes
awaits, telemetry serialization and unrelated camera/terrain work. The 0.10 ms
p99 and zero warmed allocation limits apply to the combined interval, not only
the renderer recording subset. Raw samples retain both components.

Return the **complete evidence directory**, including failed attempt logs,
manifest, raw samples/handoffs and summary/result files. `SMOKE ONLY` is a
plumbing result. `MEASURED GATES PASSED` covers only the explicitly measured
subset; `FullQualification=false` and the remaining-gate list are deliberate.
Do not edit those fields into an approval.

## Gates still requiring separate evidence

| Gate | Required disposition |
| --- | --- |
| Reference results | Run the fixed protocol on the accepted machine; inspect every required phase/pair and retain failures. |
| CPU qualification | Inspect the measured combined atmosphere preparation/recording and allocation results on the reference system. This is not overall application frame CPU or protected-work evidence. |
| Memory | Complete incremental peak GPU memory including driver backing and overlapping handoff resources. The resource ledger does not prove this by itself. |
| Protected workload | Retain WP11 movement/edit/recovery and speculative CPU/GPU pressure limits against the accepted baseline. A static warm phase is not pressure qualification. |
| Published package | Obtain the required final code approval and authorized engine merge, green master push CI and qualified immutable archive through existing policy. |
| WYU package integration | Update all ten exact package pins, lockfiles and provenance digests together, then repeat the affected installed flight/Continue routes and CI. |

Neither TW1–TW3 nor a diagnostic hash record authorizes merging, publishing a
release or weakening these gates. Current PRs remain reviewable candidates
until their required technical evidence is complete.
