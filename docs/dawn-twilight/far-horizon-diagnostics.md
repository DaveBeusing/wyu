# Far Horizon GPU and handoff investigation

The [21 November follow-up](reference-results-2026-11-21.md) contains the returned
probe, observed GPU-state differences, new wait/depth corrections and renewed
verification. Its failed reference rows remain open; this document describes the
diagnostic tool and the preceding investigation.

The [20 November reference archive](reference-results-2026-11-20.md) remains
**CHANGES REQUIRED**: eleven per-pair GPU-relative rows and four per-pair handoff
rows fail. All five pooled rows pass. No row, pair, outlier or failed attempt is
excluded by this investigation, and pooling does not replace per-pair acceptance.

## Findings and corrections

| Observation | Disposition |
| --- | --- |
| GPU query/fence identities, units and nearest-rank calculations agree with raw data. | No parser correction or retrospective promotion is justified. |
| Every new renderer owner recreated its immutable sky root signature and PSO during its first sky draw, even on the retained device session. | Retain one exact sky pipeline/root pair in the session; each owner holds independent COM references. Do not change shader, format, draw count or GPU work. Session disposal closes admission while an attached owner can finish safely. |
| GPU-owner measurement callbacks serialized JSON and synchronously wrote stdout for every retained native execution. | Buffer value events in a fixed 8,192-slot array per owner, and serialize after the held phase. Preserve owner/sequence/fence/CPU/GPU observations. Overflow fails closed, with no overwrite. |
| GPU time changes substantially between runs and sometimes within a 400-sample window. | The returned archive lacks clocks, power and scheduling evidence. Its variation cannot be attributed to the GPU driver, clock state, preemption or unrelated work with confidence. Collect correlated diagnostics. |
| Sky covers the fullscreen background before terrain. Some failed tails are inside its GPU interval. | This is genuine shader work and a possible optimization target, not proof of the cause of every relative failure. No shader quality, resolution or pass semantics are reduced here. |

For example, original orbit pair 2 sequence 1503 takes 1.517472 ms for the frame,
of which 1.448928 ms is in the sky interval; combined preparation/recording takes
only 0.0048 ms. Conversely, the off-frame median can change from approximately
0.274 to 0.503 ms within one window. Adding a sky percentile to an unrelated
off-frame percentile would therefore misattribute the overhead.

The four original handoff failures include both old-owner stop and new-owner
startup variability. The endpoint is already the first successful native Present
of the new owner. No capture delay is included: screenshot fixtures are disabled
when sky measurement is enabled. The endpoint and original +10% limit are retained.

## Diagnostic evidence

Additional hooks are opt-in and private to the paired diagnostic renderer:

- Original GPU frame/sky timestamp endpoints and frequency are copied only after
  their real fence retires. Queue clock calibration relates GPU ticks to CPU QPC.
- Buffered events include their CPU observation QPC. Retirement observation time
  is not the exact hardware completion time; GPU endpoints and calibration are
  distinct evidence.
- Handoff stages record stop/dispose/settlement, owner creation, initialization,
  start and first mask/content/frame submission. Their 64-slot value buffer is
  serialized after the native Present endpoint. Incomplete handoffs retain stages
  with `INCOMPLETE` and no invented end time.
- Each replay can collect read-only `nvidia-smi` telemetry once per second:
  GPU UUID, performance state, clocks, utilization, temperature, power and clock
  event-reason mask. UTC/QPC boundaries and the local time zone are recorded.
  Missing tools, unsupported fields, unavailable data and collector failures remain
  explicit, never numeric zeros. No driver, clock, power or display setting changes.
- `*-diagnostics.json`, `*-gpu/collector.json`, `*-gpu/stdout.csv` and stderr
  accompany the original raw logs, samples and handoffs. GPU telemetry identifies
  the device rather than the replay process alone; concurrent work remains possible.

One-second device telemetry cannot prove millisecond preemption, individual process
GPU use or actual scanout. A repeated unexplained GPU tail may require a separate
PIX/ETW scheduling capture. Such a capture is not silently substituted for these
timing gates. Forced OS termination can prevent the final event export; incomplete
runs remain blocked, and the runner retains available logs.

## Execute the focused probe

The single-file reference kit supports:

```powershell
pwsh -NoProfile -File "\\X11D\Share\Invoke-GroundworkReferenceMeasurements.ps1" -DiagnosticProbe
```

This builds the exact pinned source, verifies the original immutable packages and
stages both rendering DLLs as before. It runs **Far Horizon only**, with one off
and one on flight, native 1920 × 1080, 40 warmups and 400 retained samples per
phase/variant. It does not repeat the synthetic fixture. It records one handoff per
direction/variant instead of the 30 required for acceptance, and only one of the
five required pairs. The manifest retains those original acceptance denominators.

This probe is **DIAGNOSTIC ONLY**, even if every numeric row passes. Telemetry can
perturb scheduling; do not accept its result as the canonical performance run.
Numeric failures still produce `CHANGES REQUIRED`. Preserve the whole result ZIP
and checksum alongside earlier failed archives. The default kit invocation still
runs the complete original protocol; `-Diagnostics` without `-DiagnosticProbe`
collects the full protocol with additional telemetry, also diagnostic-only.

## Verification and remaining blocker

97 non-interactive renderer tests passed, including WARP three-owner pipeline
reuse, session disposal with an attached owner and unchanged raw GPU intervals.
Managed tests cover zero warmed allocations/output in the measurement callback,
exact event retention, capacity overflow, partial sink errors, handoff endpoints,
process-exit evidence, and collector process ownership/deadlines. These tests are
included in CI without requiring a GPU for the client-side managed regressions.

The local Far Horizon hardware diagnostic smoke completed both flights with zero
native extras. It retained all 48 raw GPU-tick rows, six clock calibrations and
50 handoff-stage records, plus successful telemetry from both replay collectors.
Sky pipeline telemetry records initial creation at 1,770.6 microseconds and reuse
on the next two owners at 1.1/1.9 microseconds. These are local observations, not a
paired before/after reference speedup. All six failed smoke budget rows remain.

The workstation collector reports RTX PRO 5000, P8, 180 MHz graphics and 405 MHz
memory during this local probe. This does not establish the state of the owner's
RTX 5070 during the original failures. It demonstrates why recording the reference
device state is necessary rather than assuming a particular clock or power cause.

**BLOCKED:** a new reference-machine probe is needed to separate the remaining GPU
tails and handoff components on that machine. After investigation/correction,
renew the complete uninstrumented reference protocol with all original per-pair,
pooled, absolute sky/CPU/allocation/draw and handoff limits. Full memory, protected
work/pressure and approved-package/installed-route gates remain open.
