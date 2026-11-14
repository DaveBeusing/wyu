# WP15 · Far Horizon sunlight and terrain shadows

The near view uses the Package E simulation tick and interpolation fraction
for sunlight. With Package G active, the sky disk supplies the exact direction
used by the terrain. The biome RGB is the material color. Package E's daylight
gain is split into ambient and direct so the material is multiplied once. The
direct share ramps from zero at the horizon to 65% at an elevation sine of
0.12, with the remainder left as ambient. At night ambient stays at the
Package E floor of 0.18 and the shadow passes are disabled. Fog still follows
terrain shading. Orbit and Ember Caves use their existing presentation.

Set WYU_WP15=on to opt into WP15. The default retains Package E lighting
pending qualification with a moving sun. Both variants use the same
immutable Groundwork archive and both retain Package B, E and G.

## Windows checks

Set WYU_WP15=on and WYU_WP15_PROBE=route and run the Release client with
--volume-smoke 42 hardware or --volume-smoke 20261111 warp.
The route derives midday, low sun and night ticks from the canonical spawn,
looks at fixed poses while moving 4 m along the heading at low sun, turns
from −0.70 to +0.70 rad, and checks presentation after rebasing to an origin
24 m ahead and adding a brush 6 m ahead. The route checks sky and light
inputs plus presentation sequence and terrain revision. The Groundwork
renderer tests cover the shadow pixels at the cascade split and the exact
first presented terrain edit. The WYU route checks do not measure rebase
edge displacement in pixels.

With WYU_WP15=on, run eng/Test-ReferenceSky.ps1 for the flight handoff and Continue. The flight
smoke output includes a WP15 LANDED row for comparing the restored lighting
to the landing pose and tick.

## Performance capture

Use eng/Measure-ReferenceSunlight.ps1 with a copy of the Release client that
contains a Groundwork renderer instrumented from commit 695db49. In a
separate Groundwork checkout at that commit, apply
eng/groundwork-wp15-timestamps.patch from WYU with git apply --unidiff-zero.
Build Groundwork.Rendering.D3D12 in Release and copy only that DLL into
the measurement copy of the WYU client directory. The package-pinned
functional build uses the verified archive as published. The
instrumentation uses direct queue timestamps around the shadow passes,
terrain pass and entire frame command list, plus render-thread CPU and
allocation counters for cascade setup, selection and pass recording. It
returns the samples by accepted frame sequence after GPU retirement. Replay
data uses seed 42, 1920 × 1080 windowed, five paired runs, 40 warm-up frames
per phase and 400 captured frames per run and variant. The off/on order
alternates. Compare the per-run and pooled budgets; the script returns a
failure if any checked budget is exceeded.

This workstation evidence is separate from WP11 reference and production
qualification. A GPU command-list interval excludes DXGI Present and DWM.
The shadow allocation counter reports the two committed depth map
allocations. The native geometry counter records prepared mesh buffers.
Packed normals add four bytes per terrain vertex in the pinned renderer.

## Workstation results

Windows x64 Release, RTX PRO 5000 Blackwell Laptop (32.0.15.9653),
Core Ultra 9 285HX (24 cores), approximately 64 GiB RAM, 1920 × 1080
windowed DXGI flip-discard with Present sync interval 1. Each row pools
2,000 frames per variant across the five paired runs.

| Phase | Extra terrain GPU p95 / p99 (ms) | Shadow passes p95 / p99 (ms) | GPU frame delta p95 / p99 | Cascade CPU p99 (ms) |
| --- | ---: | ---: | ---: | ---: |
| Midday | 2.6713 / 2.7045 | 2.4668 / 2.4931 | +114.48% / +103.77% | 0.0485 |
| Low sun | 2.6735 / 2.7314 | 2.4689 / 2.5134 | +118.58% / +135.98% | 0.0536 |
| Quick turn | 2.6782 / 2.7134 | 2.4696 / 2.4938 | +118.42% / +120.97% | 0.0329 |

Both GPU gates failed in all three pooled phases. Cascade CPU and managed
allocations passed on this route, with no measured heap bytes per warmed
shadow frame. The two maps totalled 8 MiB and issued at most two depth
draws per frame on this resident set. No additional mesh buffer was
prepared in any sampled phase. The route does not independently count
brick requests or exercise streaming pressure and retained maps at peak.

Both seeds passed the published edit and rebase route on WARP and NVIDIA
with Debug Layer and DRED. WP15-off smoke also passed on NVIDIA. The two-seed
NVIDIA flight and Continue replay matched the landing sunlight and the
Package G sky pixels at both handoffs. Clean locked restore from all ten
archive packages, nine gameplay tests, headless startup and controlled
shutdown passed.

## GPU-local geometry experiment

The original midday run spent 2.475 ms p95 in the two shadow passes.
Supplying an optimized depth-clear value alone measured 2.473 ms p95
on the same fixture. With the same clear value and terrain vertex and
index data in a GPU_UPLOAD heap, the passes measured 0.534 ms p95 in the
profiling run. The result points to mesh fetch from the original upload
heap as the dominant cost. The prototype checks OPTIONS16 and falls back
to UPLOAD where GPU_UPLOAD is unsupported. It preserves geometry size,
cascade dimensions, filtering and residency selection. Both A/B variants
use the same heap policy so the relative comparison isolates WP15.

Apply eng/groundwork-wp15-optimized-timestamps.patch in place of the
original timestamp patch to reproduce the candidate. The WYU shipping
pin remains 0.1.0-dev.43505500368.1. The optimization is an experimental
Groundwork patch pending package integration.

Five new paired NVIDIA replays used the same 1920 × 1080 Release route,
seed, driver and warm-up. Each pooled phase has 2,000 frames per variant.

| Phase | Extra terrain GPU p95 / p99 (ms) | Shadow passes p95 / p99 (ms) | GPU frame delta p95 / p99 | Cascade CPU p99 (ms) |
| --- | ---: | ---: | ---: | ---: |
| Midday | 0.7356 / 0.7631 | 0.5298 / 0.5572 | +24.57% / +23.31% | 0.0312 |
| Low sun | 0.3836 / 0.3852 | 0.1782 / 0.1787 | +15.73% / +15.64% | 0.0749 |
| Quick turn | 0.3926 / 0.3939 | 0.1792 / 0.1797 | +16.08% / +16.08% | 0.0423 |

The absolute 1.5/2.0 ms gate held in every phase and pair. The pooled
relative +10%/+15% gate failed in all phases, with midday the largest
remaining gap. Both maps still total 8 MiB. The warmed cascade section
allocated zero managed bytes and issued at most two depth draws per frame
with zero mesh rebuilds on the measured route. All six focused Groundwork
shadow tests passed with the candidate, including WARP and NVIDIA
Debug Layer/DRED readback and the first Present after an edit.

## Reuse of identical cascade projections

The GPU-local run still redrew two maps on every frame. The noon pass
p95 was 0.530 ms, versus 0.178 ms at low sun. The next candidate keeps one
rendered projection per cascade and skips recording a depth pass when
the installed mesh object and light transform are identical. No extra
depth maps are allocated. A replacement mesh invalidates both entries;
a changed transform redraws its cascade. A quick turn still redraws
when it changes a snapped projection. The terrain shader also opts into
early depth/stencil rejection.

Apply eng/groundwork-wp15-cached-timestamps.patch instead of the earlier
timestamp patches to reproduce this candidate. Five 1080p NVIDIA A/B
pairs again contributed 2,000 samples per variant and phase.

| Phase | Extra GPU p95 / p99 (ms) | GPU frame delta p95 / p99 | Pooled budgets |
| --- | ---: | ---: | --- |
| Midday | 0.1587 / 0.1753 | +5.53% / +7.44% | Pass |
| Low sun | 0.1581 / 0.1613 | +6.36% / +9.32% | Pass |
| Quick turn | 0.1661 / 0.1702 | +6.73% / +7.22% | Pass |

All absolute gates passed. The relative p99 gate failed for low sun in
pair 1 (+29.63%) and turns in pairs 3 and 5 (+15.68%, +15.73%).
The longest affected samples with terrain spikes had zero depth draws
and timestamp intervals of at most 0.000064 ms around shadow recording.
Several showed terrain intervals above 1.3 ms. The samples locate
these spikes outside cascade rendering; the timestamp intervals do not
identify the source of the stalls. The instrumented candidate passed all
six focused shadow tests on WARP and NVIDIA, including edit-first-Present
pixels. The production package pin remains the original verified archive.
These results do not qualify continuously changing shadow transforms.

## DxgKrnl correlation capture

The calibrated candidate logs six raw GPU markers with PID, logical
submission fence, sequence and the direct-queue GPU/QPC clock anchor.
Apply eng/groundwork-wp15-calibrated-timestamps.patch in place of the
previous patch. Run eng/Trace-ReferenceSunlight.ps1 with the instrumented
client from an elevated PowerShell. It captures WPR GPU activity and
writes markers-qpc.csv alongside the ETL and replay logs. The logical
fence identifies a renderer submission within that run.

Five timestamp-only 1080p NVIDIA A/B pairs reproduced individual
relative p99 failures in midday pair 4 (+16.86%), low sun pair 5
(+20.71%) and turns in pairs 4 and 5 (+15.87%, +15.84%). Pooled GPU
budgets passed. Large terrain timing spikes also occurred with WP15
disabled, including 2.314 ms in the low-sun off variant. Some long
frames had normal terrain timing. The marked intervals therefore do
not establish an expensive shadow pass or identify a scheduler cause.

The local Windows session denied both WPR GPU and direct DxgKrnl ETW.
The elevated trace and causal fix are pending.

## DxgKrnl trace and independent repeat

The administrative capture dxgkrnl-20261113-225931 contained no ETW
event loss. All five pairs passed both GPU budgets. The maximum individual
relative deltas were +9.45% p95 and +9.67% p99. Calibrated markers still
showed isolated long frames in both variants. For example:

| Frame | GPU frame / terrain (ms) | DxgKrnl correlation |
| --- | ---: | --- |
| Pair 2 on, sequence 480 | 20.025 / 0.273 | A DWM DMA packet started before the frame and completed during it. |
| Pair 5 off, sequence 110 | 8.635 / 6.176 | Two DWM DMA completions overlap the terrain interval. |
| Pair 5 on, sequence 57 | 9.408 / 0.277 | A DWM DMA completion falls before terrain start, inside the frame. |
| Pair 1 off, sequence 128 | 4.517 / 0.105 | A DMA packet submitted under ChatGPT.exe completed inside the frame. |

These samples place much of the excess outside shadow recording and show
that long terrain intervals can also occur with WP15 off. The DxgKrnl
events establish overlap, but do not establish which engine scheduling
decision or dependency delayed WYU. No corrective renderer change was
made on the basis of this trace.

A separate five-pair 1080p repeat with the same calibrated NVIDIA build
and WPR inactive passed both pooled budgets. Low sun in pair 4 remained
just above the individual relative p99 limit at +15.11%. The absolute
budget passed in every pair and phase.

| Phase | Extra GPU p95 / p99 (ms) | Relative p95 / p99 |
| --- | ---: | ---: |
| Midday | 0.1584 / 0.1612 | +6.22% / +5.26% |
| Low sun | 0.1578 / 0.1603 | +6.37% / +6.31% |
| Quick turn | 0.1658 / 0.1686 | +6.72% / +6.79% |

### Queue and fence attribution in the existing trace

Joining hardware-queue rundown to context and device rundown identifies
kernel queue 18446609420371416192 as DWM PID 2616, node 0, on the same
adapter and node as WYU's render context. Queue 18446609420346973520
belongs to CSRSS PID 1848, also on node 0. The DMA submit event can carry
a different PID from the device owner. The earlier ChatGPT.exe
observation describes the submit event, while DWM owns that queue.

| Replay / sequence | GPU frame / terrain (ms) | Nearby control frame / terrain (ms) | Queue evidence |
| --- | ---: | ---: | --- |
| 2-on / 480 | 20.025 / 0.273 | 477: 2.689 / 0.262 | DWM node-0 DMA progress fence 48928781 starts 0.548 ms before frame begin and completes 18.786 ms into the frame. |
| 5-off / 110 | 8.635 / 6.176 | 108: 2.560 / 0.107 | DWM progress fences 48946777 and 48946784 complete within the terrain timestamp window. |
| 5-on / 57 | 9.408 / 0.277 | 56: 2.837 / 0.261 | DWM progress fence 48948828 completes inside the frame, before terrain begins. |

For 5-off/110, the DWM wait packet on node 0 uses DxgSyncObject
18446609422247064512 at value 1950167. It retires at QPC 3424415444078.
The WYU present-side wait uses DxgSyncObject 18446609421330338544 at
value 110 and retires at QPC 3424415464252, after the GPU frame-end
marker. The WYU present-side wait on 2-on/480 similarly retires after
the frame-end marker. These packets use distinct sync objects. The
preceding normal 2-on/477 frame also has the same three WYU GPU wait
contexts and the same wait-object pointer as the slow frames. The
captured wait event does not expose per-object target values for that
array. No matching DWM-to-WYU wait/signal edge was established.

The shared-node DWM queue is the recurring overlap in these examples.
This supports a scheduling-contention hypothesis for both variants.
The ETL does not show that the DWM packet occupied the engine for its
entire timestamp window or establish which dependency delayed WYU.
No additional replay or renderer change was made for this trace review.

### Composed Flip versus Hardware Composed Independent Flip

PresentMon 2.6 decoded presentation modes offline from the same NVIDIA
1080p ETL. The border-decorated window switched modes during the trace.
The measured midday ranges contain both modes with shadows off and on.
Low sun and turns contain only Independent Flip after warm-up. Results
compare observed presentation modes within one capture.

The join uses the WYU PID, the preceding DxgKrnl present-side wait packet
whose fence value matches the frame sequence, and the nearest PresentMon
CPU start within -0.1 to +0.5 ms of that wait. It admits each Present once.
11,980 of 12,000 measured frames matched. DWM overlap means a matching
start/end DMA progress-fence interval on the identified DWM node-0
queue intersects the calibrated GPU frame. Counts cover that queue alone.

| Midday shadows | PresentMon mode | Frames | Frame GPU p99 (ms) | DWM overlaps | Frames above 3.8 ms |
| --- | --- | ---: | ---: | ---: | ---: |
| Off | Composed: Flip | 329 | 2.7162 | 0 | 0 |
| Off | Hardware Composed: Independent Flip | 1,671 | 2.5658 | 3 | 3 |
| On | Composed: Flip | 320 | 2.9012 | 1 | 1 |
| On | Hardware Composed: Independent Flip | 1,680 | 2.7273 | 2 | 2 |

Midday p99 is 5.5% lower off and 6.0% lower on in the Independent Flip
samples. Overlaps are too sparse to establish a reduction, and slow
frames persist in that mode. In particular 5-off/110 (8.635 ms) is
Independent Flip and overlaps DWM; 5-on/57 (9.408 ms) is Composed Flip
and overlaps DWM. The 20.025 ms 2-on/480 frame is also Independent Flip
but is in the low-sun warm-up and excluded from the table.

Independent Flip-only p99 off/on is 2.5311/2.6918 ms at low sun
and 2.5366/2.7067 ms during turns. The observed comparison is sensitive
to when Windows promoted the window and how much background work was
present. It does not isolate the presentation mode as the cause of
the lower p99. No new trace or display configuration was needed for
this within-capture comparison.

### Separate flip captures

The flip-comparison runner records separate ETLs with the same 1080p
client build and shadow-off/on route. The composed run uses a static
layered panel over the game client to request composition. The independent
run leaves the client unobstructed. PresentMon decodes both ETLs offline.
Windows can choose a different mode even when the intended setup is active.
Use only frames with the matching decoded presentation mode and report any
promotion or fallback. The layered panel itself is an additional compositor
input in the composed run. Keep the desktop quiet and use the same NVIDIA
output for both runs.

Run eng/Trace-ReferenceSunlightFlipComparison.ps1 from elevated PowerShell
with the calibrated client and PresentMon paths and a fresh output directory.
The client must include the flip probe and the calibrated renderer DLL.
The two recording folders include raw ETL, PresentMon output, calibrated
GPU markers and both shadow variants. The runner prints a completion line
after both recordings have been saved. The separately recorded comparisons
are pending.

### Separate 1080p NVIDIA captures

Both ETLs from flip-20261113-232826 decoded without event loss. The
layered panel did not hold Composed Flip. PresentMon reported 12,150 of
13,200 presents as Hardware Composed: Independent Flip in the requested
Composed capture and 12,151 of 13,200 in the requested Independent capture.
All other presents were Composed Flip. Compare the actual modes within
each capture. The two captures do not form a controlled mode A/B.

The PID/sequence/present-wait join matched 11,955/12,000 warmed frames in
the panel capture and 11,979/12,000 in the unobstructed capture. Per-trace
device and hardware-queue rundown identifies DWM Node 0 on the WYU adapter.
Overlap counts use paired DMA start/end events for those queues.

| Capture | Shadows | Midday p99 Composed / Independent (ms) | DWM overlaps Composed / Independent |
| --- | --- | ---: | ---: |
| With panel | Off | 2.7168 / 2.5716 | 0/305 / 6/1,670 |
| With panel | On | 2.8823 / 2.7267 | 2/325 / 0/1,675 |
| Unobstructed | Off | 2.6392 / 2.5661 | 0/310 / 1/1,690 |
| Unobstructed | On | 2.8066 / 2.7271 | 0/319 / 2/1,681 |

The observed midday p99 is lower under Independent Flip in all four
comparisons. Overlap counts show no consistent decrease. The panel capture
includes a 19.4828 ms Independent Flip frame with shadows off and DWM
overlap, as well as a 9.2853 ms Composed Flip frame with shadows on and DWM
overlap. Low sun and turns had only Independent Flip among matched frames,
with zero DWM overlaps in either capture. Their p99 values were effectively
unchanged across captures: about 2.531/2.692 ms off/on at low sun and
2.536/2.707 ms during turns. These observations do not establish that
changing the presentation mode reduces DWM overlaps or long-frame risk.

The clipped-window capture flip-clip-20261113-235034 saved successfully.
The ETL has zero reported event loss and 13,220 WYU DXGI Present starts
and matching counts of Present stops and DxgKrnl PresentHistoryDetailed
starts. PresentMon classified only DWM output even with its process filter
removed. Treat the WYU presentation mode in this capture as unverified.
The capture wrapper now preserves the ETL and continues after a missing
PresentMon export. Its Modes parameter can resume only the missing arm.

### Clipped and unobstructed 1080p captures

Both captures in flip-clip-20261113-235034 decoded with zero reported
ETW event loss. In the unobstructed arm, PresentMon exported 12,148
Hardware Composed Independent Flip and 1,052 Composed Flip presents
for the WYU PIDs. In the clipped arm, no WYU presents were classified
by PresentMon. Raw DxgKrnl records 13,220 PresentHistoryDetailed starts
with Model 2 (redirected flip), matching the DXGI start and stop counts.
That token model identifies the presentation submission path. It does
not establish the eventual scan-out mode for each frame.

The following comparison uses all warmed GPU markers (2,000 frames
per row and arm). Counts include frames intersecting at least one
matched DMA interval from a DWM Node-0 hardware queue on the WYU
adapter. Queue ownership was resolved independently from each ETL.

| Phase | Shadows | Clipped p99 (ms) | Unobstructed p99 (ms) | Clipped / unobstructed DWM overlaps |
| --- | --- | ---: | ---: | ---: |
| Midday | Off | 10.8545 | 2.6546 | 48 / 7 |
| Midday | On | 4.7986 | 2.8093 | 19 / 3 |
| Low sun | Off | 10.8596 | 2.5315 | 43 / 0 |
| Low sun | On | 4.8991 | 2.6918 | 15 / 0 |
| Turn | Off | 4.9279 | 2.5360 | 18 / 0 |
| Turn | On | 3.4224 | 2.7068 | 13 / 0 |

Within the unobstructed trace, mode-verified midday p99 for
Composed versus Independent Flip was 2.6987 versus 2.5658 ms
off and 2.8305 versus 2.7275 ms on. DWM overlap counts were
4/309 versus 3/1691 off and 0/323 versus 3/1677 on. All
classified low-sun and turn frames used Independent Flip. Twenty
turn samples lacked a matching PresentMon row and were excluded
from mode-specific comparisons only.

The unobstructed arm had lower p99 and fewer DWM overlaps for both
shadow states. Because the clipped arm has no per-present scan-out
classification, this is a comparison of the two window setups.
The lower clipped-arm p99 with shadows enabled is an observation
across separate wake and scheduling windows, not evidence that
shadows speed up rendering.

## Verified archive with GPU-local meshes and cascade reuse

The consumer pins 0.1.0-dev.43527092534.1, produced by green Groundwork
master 20871f43a809aa81c6347e4ab93b163fca9df1df (Windows CI 43527092534,
attempt 1). Fresh locked restore of all ten packages, nine gameplay tests,
headless startup and controlled shutdown passed.

Five Windows Release A/B pairs used the existing seed 42 and three-phase
replay at 1920 × 1080 on the NVIDIA RTX PRO 5000 Blackwell Laptop. The
qualification copy used the exact producer source with the
eng/groundwork-wp15-archive-timestamps.patch instrumentation applied via
git apply --unidiff-zero. Only the measured copy used that DLL. It retains
the merged renderer and adds timing and counters, without the experimental
early-depth shader attribute or optimized clear from the previous probe.
Each pooled phase contains 2,000 samples per variant.

| Phase | Extra GPU p95 / p99 (ms) | Relative GPU frame p95 / p99 |
| --- | ---: | ---: |
| Midday | 0.1585 / 0.1623 | +5.92% / +7.49% |
| Low sun | 0.1579 / 0.1617 | +6.36% / +15.02% |
| Turn | 0.1661 / 0.1696 | +6.72% / -6.89% |

The absolute 1.5/2.0 ms budget passed in all pairs and phases. Relative
p95 passed everywhere. Relative p99 exceeded +15% in midday pair 3
(+83.45%) and low-sun pair 2 (+28.83%); the pooled low-sun result also
missed at +15.02%. The timing-only run does not establish the cause
of these outliers. The flip investigation stays closed.

### Outlier review and five-pair repeat on the pinned source

At midday in pair 3, the fifth-slowest shadow-on sample (344) set p99:
4.9375 ms frame, 2.3657 ms terrain, zero cascade draws. The maximum
(sample 21) reached 15.2769 ms with 12.7665 ms inside the terrain
timestamp interval. Two other slow samples had ordinary terrain
intervals of approximately 0.25 ms. At low sun in pair 2, sample 305
set shadow-on p99 at 3.2638 ms with 0.2615 ms terrain and zero cascade
draws. The largest shadow-off frame in that phase took 4.5345 ms, of
which 2.1129 ms fell inside its terrain interval.

These timestamps locate the delays relative to the draw markers.
They cannot distinguish extra GPU execution from queue interruption
without a simultaneous scheduler trace. Prior DxgKrnl overlap findings
are from different runs. No renderer fix is claimed.

A fresh set of five 1080p NVIDIA pairs used the same WYU binary and
same timing-only renderer built from the pinned 20871f4 archive source.
All 15 individual phase checks and all three pooled phase checks passed
both GPU budgets. The previous excursions did not recur at p99.

| Phase | Extra GPU p95 / p99 (ms) | Relative GPU frame p95 / p99 |
| --- | ---: | ---: |
| Midday | 0.1583 / 0.1610 | +6.22% / +4.61% |
| Low sun | 0.1577 / 0.1596 | +6.37% / +6.31% |
| Turn | 0.1658 / 0.1684 | +6.74% / +6.64% |

### Moving-sun review replay

The corrected replay advances the accepted sun tick every frame and
alternates exact and half-tick samples. The turn phase oscillates
between +/-0.70 rad with a 24-frame period. Both variants use the same
route and pinned Groundwork 0.1.0-dev.43527092534.1 source; the probe DLL
adds timestamps and counters. All five 1080p NVIDIA pairs rendered
cascades in 400/400 measured frames per enabled phase and 0/400 with
WP15 disabled. Present mode was not independently classified in this
timestamp-only replay.

| Phase | Extra GPU p95 / p99 (ms) | Relative GPU frame p95 / p99 |
| --- | ---: | ---: |
| Midday | 0.7508 / 0.7914 | +29.62% / +25.98% |
| Low sun | 0.3782 / 0.3805 | +15.59% / +15.67% |
| Repeated turns | 0.3816 / 0.3852 | +15.55% / +15.75% |

The absolute GPU budget passed in all pairs and phases. The relative
GPU budget failed in every pair and phase. WP15 is opt-in via
WYU_WP15=on pending performance qualification; the default preserves
Package E lighting.
