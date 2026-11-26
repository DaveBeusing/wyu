# Lookup resource reuse reference series, 21 November 2026 at 21:22

**CHANGES REQUIRED for both configurations.** The archive
`ReferenceResults-20261121-212256-1bec2568a5bc41d6aa13944cbdda5f9b.zip`
matches its adjacent SHA256
`01806C64561BC0F35855791E5E7DBE8E486DEAC5948CA635D97511317905F859`.
All 1,742 files in the inventory match their hashes and sizes. The executed
kit is `C611914165AF5CF1FF20644A89E0C6B554DDC88C615E54D092F4E3EBB5793008`.
Its Groundwork and WYU inputs are `7e1924c2a31028ef46d98e70507acd1c030dfb0c`
and `9f1424e517aa057e247b0a2d7e4b5b44a42e205f`. The renderer is the
controlled diagnostic overlay. The kit was read as evidence.

Both configurations completed 60 flights at 1920 × 1080 on the reference
i9-10850K / RTX 5070. All 24,000 frame timings, 240 handoffs and 60 summary
rows reconcile independently from native ticks and QPC. No failed flights
or extra native executions were reported. All six canonical phase identities
match the [19:12 full series](reference-series-2026-11-21-191232.md).

| Pooled comparison | Far Horizon 19:12 → 21:22 | Orbit Combined 19:12 → 21:22 |
| --- | ---: | ---: |
| Surface frame p95 / p99 | −0.27% / −6.50% → +4.29% / **+9.48%** | +1.21% / +10.45% → **+9.34%** / +7.42% |
| Ascent frame p95 / p99 | +18.12% / +48.37% → **+7.04% / +11.36%** | +8.98% / +0.62% → **+21.43%** / +4.07% |
| Orbit frame p95 / p99 | +4.45% / −4.77% → **+5.07%** / +5.75% | +0.14% / +0.07% → +1.71% / +1.11% |
| To Orbit handoff p95 | +7.50% → +2.17% | −1.93% → +0.36% |
| To ground handoff p95 | −0.89% → +8.83% | +14.13% → −7.21% |
| Failed comparison rows | 11 → **15 of 30** | 16 → **11 of 30** |

Bold numeric entries fail the original +5%/+8% frame and +10% handoff
limits. Each pair must also pass. Far Horizon fails surface pairs 2/5
and pooled, all Ascent rows, Orbit pairs 2/4/5 and pooled, outbound pair 3
and return pair 1. Orbit Combined fails all surface rows, Ascent pairs
1/3/4 and pooled, and Orbit pair 1. All Orbit Combined handoffs pass.
Absolute sky and CPU limits, one draw and zero warmed allocations
pass throughout. Full memory, pressure, visual and installed-package
checks remain open.

## Actual reuse and remaining handoff cost

Each of the 30 enabled flights in each configuration reports actual
reuse for owners 2 and 3. The off arms report none. That is 120 actual
handoff reuses across the series.

| Median inclusive Lookup readiness | 19:12 | 21:22 |
| --- | ---: | ---: |
| Far Horizon, owner 2 / 3 | 0.4651 / 1.0943 ms | 0.0020 / 0.0016 ms |
| Orbit Combined, owner 2 / 3 | 0.4366 / 1.1338 ms | 0.0017 / 0.0016 ms |

Orbit Combined's pooled ground return improves to −7.21% and its required
per-pair rows pass. In Far Horizon the largest remaining handoff failure is
outbound pair 3, replay 6 in each arm, 27.4486 versus 40.2436 ms.
The renderer start interval grows from 5.8419 to 17.7907 ms, accounting
for 11.9488 ms of the 12.7950 ms difference. Submit to Present rises
only from 0.7516 to 0.8500 ms. Return pair 1 likewise has a longer start
interval (6.0523 versus 8.0760 ms for the p95 representatives).
The original start interval includes native device/surface setup and
thread scheduling. No existing data identifies which of those costs is
responsible.

## GPU findings and focused diagnosis

Orbit Combined's on pre-sky p95 alone reaches 0.710272 ms on surface and
0.969472 ms in ascent, against entire off frames of 0.662912 and
0.841920 ms. Both already exceed the +5% limits. Far Horizon's usual
Ascent sky interval remains close to 50 µs. Its pair 3 pre-sky p95
is 0.742976 ms versus 0.443616 ms for the entire off frame. Coarse
telemetry samples a different memory state near part of this block.
Orbit Combined surface pair 4 shows pre-sky growth even at comparable
sampled memory clock. The archive contains no ETW scheduling capture.
The timing and clock observations leave preemption and driver or
pass-specific attribution unresolved.

The short `-GpuPassBreakdown` probe exports the existing GPU intervals
for terrain and near/far shadow passes. Rows must match exact native
owner, sequence, fence and phase in both arms. The diagnostic also marks
when native device initialization completes within the existing
renderer-start interval. It adds no new GPU queries or timestamp edges;
untimed clear, props, barriers and other work remain in the frame interval.
A zero shadow interval means no timed draw that frame, including when a
valid cascade is reused.
Use the original whole-frame and handoff endpoints for acceptance.
