# Reference follow-up — 21 November 2026

The later [12:15 reference probe comparison](reference-probe-2026-11-21-121552.md)
records the corrected candidate: handoffs pass in the short probe, while all
three relative GPU frame comparisons still fail. The analysis below describes
the preceding evidence and corrections.

**CHANGES REQUIRED / BLOCKED.** The new data does not close the original fifteen
Far Horizon failures. It identifies real GPU-state differences, sparse submission
cadence and remaining shutdown polling. The corrections below retain every original
limit and require renewed measurements; none retrospectively changes a failed row.

## Verified archives

All three adjacent SHA-256 files matched. Every inventoried file matched its size
and SHA-256; local archive copies are retained. Embedded scripts were inspected as
evidence, not executed.

| Archive suffix | SHA-256 | Inventoried files | Recorded source |
| --- | --- | ---: | --- |
| `20261120-203714-b3a175ad7fd5416b934fc378408cb451` | `B6CC84F3036609846700400AAE5AB284C234AE167FE4CAD6A554951ADE998BF0` | 1264 | Groundwork `eb9c7833`, WYU `e6b605b` |
| `20261121-100917-297798ad791f40179f3fc48d15b18352` | `9D5E9049997DDA5962F72707CC10C1F32657967AD5439D9F58A89F377AB829F3` | 791 | Groundwork `5fad417a`, WYU `da3facea` |
| `20261121-110837-7e0aab2b8a4b4d289704b86b3c4cb0d4` | `E5D6E97B2E6661BABA8151B379D75179365F0F1AD7B9842C3844C750E6C20C91` | 81 | Groundwork `5fad417a`, WYU `da3facea` |

Names have prefix `ReferenceResults-` and suffix `.zip`. The first archive completed
60 flights per composition, with 13 failed Far Horizon and six failed Orbit summary
rows. The second completed the synthetic subset and all 60 Far Horizon flights,
with **18 failed rows**: twelve GPU per-pair, three GPU pooled, and three handoff
rows. Orbit has no terminal result in that archive; the parent command has no exit
code. This does not prove the reason for interruption. Its old parent summary's
`Not started` aggregate is incorrect because Far Horizon had already completed.

The latest archive is the deliberately reduced, instrumented probe: two successful
flights, no native extras, 400 retained samples per phase and variant. Eight summary
rows fail: three GPU phase comparisons and the return handoff, each appearing as
both the single pair and its identical pooled row. These are not eight independent
experiments. This probe never satisfies full acceptance denominators.

## GPU findings

Queue calibration and phase QPC/UTC anchors agree with the collector's local
UTC+01:00 timestamps. GPU timestamp frequency is distinct from graphics MHz.

| Probe observation | Off | On |
| --- | ---: | ---: |
| Orbit frame median | 0.269728 ms | 0.502880 ms |
| Orbit memory/performance state | P3 / 7001 MHz | switches to P5 / 810 MHz |
| Ascent frame p99 | 0.573536 ms | 0.941632 ms |
| Ascent memory/performance state | P3 / 7001 MHz | P3 / 7001 MHz |

The memory/performance-state difference is observed, not assumed. It is not a
complete cause: ascent frame p99 still increases by 64.180%, above the +8% limit,
with the same sampled memory state. The first orbit tail also precedes the first low-state
telemetry sample. One-second telemetry cannot identify the exact clock/preemption
event inside an individual millisecond GPU interval.

GPU end-to-next-start gaps have medians of **15.49–15.72 ms**, while frame work is
usually 0.27–0.50 ms and device utilization is only 2–4%. The held measurement path
still used an additional `Task.Delay(8)` polling loop for presentation acknowledgement.
This fixture pacing is confirmed. It is distinct from the unchanged native
`Present(1, 0)` policy and cannot be corrected by filtering high samples.

Tails are not a uniform slowdown of all shader work. One ascent outlier has
pre-sky/sky/post-sky intervals of 0.044160/1.072224/0.002208 ms; normal medians are
approximately 0.016/0.309/0.074 ms. Original pass intervals may contain scheduling,
cache and pipeline interactions. Do not subtract unrelated off/on percentiles or
assert a particular driver defect from these observations.

## Handoff findings

The previous PSO correction is effective on the reference GPU: initial sky pipeline
creation takes 1.0128 ms; the next two owners reuse it in 1.2 and 1.0 microseconds.
The probe's ascent handoff passes at +1.173%. Return still rises from 7.9888 to
9.1870 ms (+14.9985%, only one observation per variant): approximately +0.7484 ms
lies in previous-owner stop/dispose and +0.4791 ms in renderer start. Submit through
native Present is slightly faster in the on variant. Repeated sky PSO creation is
therefore not the remaining return failure.

Source inspection found a separate ten-millisecond shutdown poll waiting for GPU
retirement without arming the already available native fence wake. This is a real
avoidable wait. It is not proof that every observed stop/start variation comes
from that poll; runtime settlement's five-millisecond fallback was not implicated
by these stage records.

## Corrective implementation

1. Held sampling now awaits the renderer's exact sequence acknowledgement through
   the existing presentation-wait API. The fixed 30-second bound, lifecycle/fault
   checks and caller cancellation remain. Timeout and cancellation explicitly cancel
   the underlying wait, including the cancellation-registration/disposal race.
2. Shutdown arms the existing retirement fence event and rechecks idle/removal
   before waiting. The ten-millisecond fallback and signal-failure/sentinel custody
   protections remain; no resource is released before retirement or removal proof.
3. Render opaque terrain, props and FollowOn before sky. Sky uses the existing
   depth target through a read-only DSV, `LESS_EQUAL` at depth one and early depth
   testing, so covered pixels skip the sky shader. Atmospheric integration, sun,
   stars, sample counts, resolution and visible output are unchanged. There is no
   additional target, draw or upload: only a second bounded CPU descriptor for the
   same depth resource. DepthWrite is restored before readback/next frame.
4. The standalone runner checkpoints the active composition as blocked before
   launch and updates the aggregate after each completed composition. An interrupted
   Orbit run can no longer erase a completed Far Horizon failure into `Not started`.

No extra GPU load, busy loop, power-policy change or clock locking is introduced.
The [full reference protocol](reference-qualification.md) and its numerical limits
remain unchanged. The [focused diagnostic mode](far-horizon-diagnostics.md) remains
explicitly non-qualifying.

## Verification and remaining work

The non-interactive renderer suite passes **99/99** tests. Five focused owner-wake
tests pass separately, covering both completion races, empty submission and
signal/sentinel/removal fallback. Managed presentation-wait tests verify actual
underlying cancellation, acknowledgement identity, faults, timeout, one event wait
instead of polling, and the unchanged production 30-second bound.

Five WARP and five NVIDIA image fixtures (day, horizon, dusk, night, orbital sun) compare every RGBA
pixel with independently rendered opaque-over-background composition. All pairs
are bit-identical, all depth values remain identical, and resized odd extents plus
FollowOn are included. These are fixed renderer fixtures, not a complete gameplay
visual qualification. The WARP comparison reports **15 warnings, all ID 820**, from the
pre-existing offscreen helper creating color resources without optimized clear
values. They contain zero errors, corruption, discarded messages or live children;
no warning filter or acceptance assertion was weakened.

The NVIDIA depth-composition, existing sky/rebase and twilight regressions pass
3/3. Local Far Horizon and Orbit hardware smokes each complete both variants with
zero failed attempts or native extras. Each retains two failed budget-summary
rows; `SMOKE ONLY` is not a performance pass. Their logs are in
`artifacts/far-depth-event-smoke/` and `artifacts/orbit-depth-event-smoke/`.

The interrupted-run checkpoint regression passes alongside the existing standalone
runner failure/ZIP/hash tests. The native smokes cover the recorded diagnostic
working snapshot; final commit and CI evidence must be bound separately in
PR #193 / #83 before technical approval of the source correction. A new reference-machine comparison is
still necessary to determine whether the unchanged GPU and handoff limits pass.
All earlier failed archives, full memory, protected-work/pressure and approved
package/installed-route requirements remain in the acceptance record.
