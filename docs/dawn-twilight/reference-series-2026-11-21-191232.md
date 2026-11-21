# Full Lookup reference series, 21 November 2026 at 19:12

**CHANGES REQUIRED for both configurations.** The ZIP
`ReferenceResults-20261121-191232-4cd6b8cdd7ba4c6a948c399083b9cb0c.zip`
matches SHA256
`5586F0E47A6F6745DD6FCEF2643526892EB55E3192C02D6D4E74010F0838FEC5`.
All 1,742 inventory entries match their recorded sizes and hashes. The
archived script was read as evidence. Its hash matches the deployed kit
`65FCD8051FBD49BC49BCFFAC94B678371A0B0B312E16156420E1D0669EDAA4E2`.
Measured inputs are Groundwork `83397a7f23fd2d576c943ce4c6580b909f7ba7b0`
and WYU `665d7ba36116fcbec8c1fcb51cc094406db5d5ea`, with matched diagnostic
renderer DLLs on i9-10850K / RTX 5070, driver `32.0.16.1088`, at 1920 × 1080.

Both configurations complete all 60 flights. An independent reconstruction
from raw ticks and QPC verifies 24,000 retained frame timings and 240
handoffs, with zero failed flights or extra native executions. Each
configuration contributes 400 frames per pair, phase and variant
(2,000 pooled), and six handoffs per pair, direction and variant (30
pooled). Canonical identities and split timing sums reconcile.

## Original budgets and results

| Pooled comparison | Far Horizon | Orbit Combined | Limit |
| --- | ---: | ---: | ---: |
| Surface frame p95 / p99 | −0.27% / −6.50% | +1.21% / **+10.45%** | +5% / +8% |
| Ascent frame p95 / p99 | **+18.12% / +48.37%** | **+8.98%** / +0.62% | +5% / +8% |
| Orbit frame p95 / p99 | +4.45% / −4.77% | +0.14% / +0.07% | +5% / +8% |
| Handoff to Orbit p95 | +7.50% | −1.93% | +10% |
| Handoff to ground p95 | −0.89% | **+14.13%** | +10% |

Far Horizon fails 11 of 30 rows. Ascent fails in all five pairs and pooled.
Orbit p99 fails pair 5 (+72.27%). Both handoff directions fail pairs 2 and 5.
Orbit Combined fails 16 of 30 rows. Surface fails pairs 1, 2, 4 and 5 and
pooled. Ascent fails pairs 1, 3, 4 and 5 and pooled. Orbit p99 fails pair 2
(+9.67%). Return handoff fails pairs 1–4 and pooled. A pooled pass does not
override an individual pair failure.

All absolute sky and combined CPU limits pass. Each enabled sky recording
has one draw and zero warmed allocations. These are measured diagnostics.
Full driver-backed memory, protected pressure, owner visual approval and
the approved installed route still require independent evidence.

## GPU intervals

Far Horizon Ascent has a stable typical pre-sky interval (about 412 µs in
both arms) and about 50 µs of on sky work. Its pooled p95/p99 off frame is
432.448/533.792 µs and on frame is 510.816/791.968 µs. The on pre-sky
interval alone reaches 457.056/594.880 µs, above both whole-frame limits.
Orbit Combined Surface pre-sky intervals alone also exceed pooled p99;
Orbit Combined Ascent pair 4 exceeds both limits before sky. These
subtractions identify where time accrued. The measured complete frame
remains the acceptance metric.

Far Horizon Orbit pair 5 is different. Its nine sky intervals above
0.15 ms all occur on sequence residue 2 modulo 15, at roughly 200 ms
spacing or its multiples. The pair's full on p99 is 886.848 µs against
514.816 µs off. Its on pre-sky p99 is 552.224 µs. No scheduling trace
was collected in this full series. Power telemetry and sequence cadence
cannot assign these tails to preemption, display, driver or shader effects.
Keep all samples and the original denominators.

## Handoff intervals

Orbit Combined pooled return p95 changes from 9.9021 to 11.3014 ms.
Median submit-to-Present grows from roughly 0.463 to 1.641 ms. Median
on preparation of the fresh Lookup texture and descriptor heap is
1.134 ms and lies inside the measured handoff. The pooled on representative
takes about 1.099 ms in that preparation. Other delays remain. For example,
Orbit Combined return pair 1 is 9.8854 ms off and 15.0452 ms on, with
previous-owner disposal of about 3.837 and 7.381 ms. Texture reuse alone
cannot explain or reliably eliminate that pair's excess.

The earlier [17:13 probe](reference-probe-2026-11-21-171306.md) used one
Far Horizon pair. Its passing rows remain valid observations. The complete
series covers more handoffs and both configurations and supersedes it
for current readiness.

## Follow-up

The opt-in `-ReuseLookupResources` candidate addresses repeated handoff
preparation. A session can hold one completed 1 MiB lookup array and its
views. A successor takes the bundle only after the predecessor's healthy
cleanup is confirmed. The active owner and the parked session account for
their respective ownership. Startup faults, stale or unconfirmed transfers,
and direct owners cannot acquire the stored bundle. A genuine completed
fence is required. The device-removed fence sentinel cannot qualify it.
See [D3D12 fence completion semantics](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12fence-getcompletedvalue).

Both slices still regenerate on every sky frame. The original handoff
timestamp includes all remaining work. The pressure script strips both
prototype flags from its installed child. The candidate still needs the
full RTX 5070 timing comparison. It addresses an identified resource cost
and leaves the pre-sky and periodic GPU failures open.

The tested exact-zero celestial blend experiment was byte-identical in all
212 image and mask comparisons on WARP and NVIDIA. It was set aside because
there is no demonstrated timing gain and it cannot remove pre-sky costs.
The unchanged shader is used for the handoff candidate.
