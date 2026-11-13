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
