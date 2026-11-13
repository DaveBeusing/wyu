# WP15 · Far Horizon sunlight and terrain shadows

The near view uses the Package E simulation tick and interpolation fraction
for sunlight. With Package G active, the sky disk supplies the exact direction
used by the terrain. The biome RGB is the material color. Package E's daylight
gain is split into ambient and direct so the material is multiplied once. The
direct share ramps from zero at the horizon to 65% at an elevation sine of
0.12, with the remainder left as ambient. At night ambient stays at the
Package E floor of 0.18 and the shadow passes are disabled. Fog still follows
terrain shading. Orbit and Ember Caves use their existing presentation.

Set WYU_WP15 to off for the Package E comparison. Both variants use the same
immutable Groundwork archive and both retain Package B, E and G.

## Windows checks

Set WYU_WP15_PROBE to route and run the Release client with
--volume-smoke 42 hardware or --volume-smoke 20261111 warp.
The route derives midday, low sun and night ticks from the canonical spawn,
looks at fixed poses while moving 4 m along the heading at low sun, turns
from −0.70 to +0.70 rad, and checks presentation after rebasing to an origin
24 m ahead and adding a brush 6 m ahead. The route checks sky and light
inputs plus presentation sequence and terrain revision. The Groundwork
renderer tests cover the shadow pixels at the cascade split and the exact
first presented terrain edit. The WYU route checks do not measure rebase
edge displacement in pixels.

Run eng/Test-ReferenceSky.ps1 for the flight handoff and Continue. The flight
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
