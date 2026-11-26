# Lookup reuse reference series, 22 November 2026 at 09:31

**CHANGES REQUIRED for both configurations.** All 120 flights completed.
The archive `ReferenceResults-20261122-093150-51ebc69f543e46f99d8c2df3b4838d30.zip`
matches SHA256 `4B97B1C9BC6A42A108AC7BE3D2BDBB6343EC734109B8A733B31D71638B6CCC72`.
All 1,742 inventoried files match their sizes and hashes. Raw GPU and QPC
events reconstruct all 24,000 retained frame samples, 240 handoffs and 60
comparison rows. There were zero failed flights and zero extra native
executions. Canonical phase identities match the 21:22 series. The renderer
was the diagnostic overlay for Groundwork `d6b3f0bf3192b807dd38144f035d3fda1cb2d27e`
and WYU `0849eea25cfe64da1984b54570fc755aeb0aeef5`.

| Pooled comparison | Far Horizon | Orbit Combined | Limit |
| --- | ---: | ---: | ---: |
| Surface frame p95 / p99 | +1.42% / +3.16% | +3.33% / +1.31% | +5% / +8% |
| Ascent frame p95 / p99 | **+57.82% / +37.36%** | +3.12% / +1.98% | +5% / +8% |
| Orbit frame p95 / p99 | +4.74% / +1.26% | −0.26% / +1.49% | +5% / +8% |
| To Orbit handoff p95 | +3.01% | −13.66% | +10% |
| To ground handoff p95 | −2.53% | −40.51% | +10% |
| Failed comparison rows | 9 of 30 | 7 of 30 | 0 |

Each pair is also required. Far Horizon fails surface pair 5 p99, all
five Ascent pairs and the pooled Ascent row, Orbit pair 4, and the
pair-5 ground handoff. Orbit Combined fails surface pairs 1 and 3,
Ascent pairs 2 through 5, and Orbit pair 3 p99. This improves the count
from 15 and 11 in the [21:22 series](reference-series-2026-11-21-212256.md).
Absolute sky and combined CPU limits, zero warmed allocations and one
sky draw pass. All 120 enabled handoffs actually reuse Lookup storage.

## Frame timing

Far Horizon Ascent pooled frame p95 rises from 0.504256 to 0.795808 ms.
Ranking only the native interval up to SkyStart still gives +47.06% at
p95 and +12.24% at p99 relative to the disabled full frame. Pair 2 has
73 enabled samples near coarse telemetry at 810 MHz memory, with a
0.748192 ms pre-sky median versus 0.430592 ms near 7001 MHz. The sampling
cadence limits attribution to the hardware state of any individual frame.

Ascent pairs 1 and 4 each contain twelve enabled sky intervals above
150 µs. Their sequence positions recur at multiples of 15 frames, roughly
200 ms. At frame p99 in pairs 1, 3 and 4 the dispatch-marker-to-sky-end
interval is 636.896, 316.128 and 627.072 µs respectively. The interval up
to the dispatch marker stays near 4.5 µs. The full series lacks ETW and
draw-edge instrumentation, so the cause of these periodic spans remains
open. A Far Horizon short probe with pass breakdown and a scheduling trace
can distinguish pass boundaries and look for scheduler activity.

On the reference machine, use PowerShell 7.4 or later as Administrator:

```powershell
pwsh -NoProfile -File "\\X11D\Share\Invoke-GroundworkReferenceMeasurements.ps1" -DiagnosticProbe -DiagnosticComposition far-horizon -LookupPrototype -ReuseLookupResources -GpuPassBreakdown -GpuSchedulingTrace
```

The archive-tested kit on the share keeps the 09:31 runtime pins for this
comparison. Check trace coverage for the affected Ascent frames.

Orbit Combined surface pairs 1 and 3, Ascent pair 4, and Orbit pair 3
p99 already exceed the whole-frame gate before SkyStart. In other Ascent
pairs, the sky interval contributes to the gate crossing. All acceptance
rows use the original full interval and include every retained sample.

## Ground handoff outlier

Far Horizon pair 5 ground handoff p95 is 10.4367 ms off and 37.3885 ms on
(+258.24%). With six flights per arm, this is the maximum in each arm.
The 5 other enabled handoffs complete in 9.7598–10.6307 ms. The outlier's
renderer startup takes 33.1283 ms. The p95 baseline startup takes 6.1867 ms.
Lookup storage is already reused and becomes ready in 2.1 µs after startup.
The existing `gpu-device-initialized` diagnostic marker is now collected in
full diagnostic runs to separate device initialization from the remainder of
renderer startup. Native Present endpoints and handoff limits are retained.

The reference performance gates remain open. The resource-usage, memory
pressure, visual and approved installed-package gates require their own
evidence. A merged integration commit needs separate verification on its
exact binaries.
