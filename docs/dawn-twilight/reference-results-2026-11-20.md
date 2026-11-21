# Reference results — 20 November 2026

## Recorded candidate and evidence

**Overall: CHANGES REQUIRED.** The synthetic renderer subset passed. Far Horizon
failed required per-pair relative limits; Orbit combined did not complete.
No merge, immutable publication or full qualification follows from this run.

- Archive: `ReferenceResults-20261120-191704-27a0ae2537234d8f939a85b2b70da837.zip`.
- Archive SHA-256: `4D3218D2B6CC3FB627E6A89410AF53E68E6C8C189BA3D5D5678644694D9B6538`.
- The adjacent SHA-256 matched; all 672 inventoried files matched their recorded
  sizes and SHA-256 values. The received archive was preserved locally.
- Executed runner SHA-256: `508173C333C120D639D978039FC22BBFD4399588B57FF16175493145E54E203D`.
- Groundwork: `eb9c783300d5252e077ea2dd2188b995d0bfc4bd`, PR
  [Groundwork #193](https://github.com/DaveBeusing/Groundwork/pull/193).
- WYU: `85f376a3afa3ae87fa4ccfeb339026231c55526d`, PR
  [WYU #83](https://github.com/DaveBeusing/wyu/pull/83).
- Reported CPU/GPU: i9-10850K / RTX 5070, driver `32.0.16.1088`.
  Windows build `26320`; PowerShell `7.6.10`; .NET SDK `10.0.404`.
- The native measured extent was 1920 × 1080. Reported desktop dimensions do
  not establish compositor scanout or presentation-mode qualification.

The immutable consumer archive remains `0.1.0-dev.44510519303.1`. Its verification,
fresh locked restore, Release build and 13 gameplay tests passed before the
controlled two-DLL diagnostic overlay. Source, client and rendering hashes agree
between the runner, build-input record and composition manifests. These diagnostic
DLLs do not establish approved-package or installed-route acceptance.

## Synthetic Groundwork fixture — PASSED for its measured subset

All 60,000 expected samples and 1,920 warmups completed. No rows were uncollected.
The table reports the largest percentile across the phase's five pairs and pooled
row, rather than selecting the most favourable pair.

| Phase | Maximum sky p95 | Maximum sky p99 | Maximum recording CPU p99 |
| --- | ---: | ---: | ---: |
| Surface | 0.257472 ms | 0.765824 ms | 0.0312 ms |
| Ascent | 0.295360 ms | 0.940640 ms | 0.0284 ms |
| Orbit | 0.153184 ms | 0.841632 ms | 0.0210 ms |

The sky limits are 0.8/1.2 ms and the fixture CPU limit is 0.10 ms. Warmed
recording allocations were zero. This synthetic CPU interval excludes application
preparation; its resource ledger does not close the full incremental GPU-memory gate.

## Far Horizon — CHANGES REQUIRED

All 60 flights completed: 12,000 retained phase samples (2,000 per phase and
variant), 120 handoffs (30 per direction and variant), and zero native extras.
Independent raw nearest-rank recalculation agrees with the runner. German comma
decimals in CSV were parsed explicitly; no percentile or unit-conversion defect
was found.

All five pooled rows passed, but **15 required per-pair rows failed**: eleven GPU
relative rows and four handoff rows. Pooling cannot replace the per-pair gates.

| Metric | Pooled result | Limit |
| --- | ---: | ---: |
| Surface total GPU p95 / p99 delta | +0.307% / +4.912% | +5% / +8% |
| Ascent total GPU p95 / p99 delta | −0.095% / −0.077% | +5% / +8% |
| Orbit total GPU p95 / p99 delta | −0.022% / +3.694% | +5% / +8% |
| Handoff to orbit p95 delta | +7.119% | +10% |
| Handoff to ground p95 delta | +5.828% | +10% |

Worst required per-pair failures:

| Metric | Pair | Off → on | Delta | Limit |
| --- | ---: | ---: | ---: | ---: |
| Orbit total GPU p95 | 2 | 0.342464 → 0.537568 ms | +56.971% | +5% |
| Orbit total GPU p99 | 2 | 0.443872 → 1.074208 ms | +142.009% | +8% |
| Handoff to orbit p95 | 4 | 26.6303 → 33.9428 ms | +27.459% | +10% |
| Handoff to ground p95 | 2 | 8.7152 → 10.8943 ms | +25.003% | +10% |

All absolute sky, combined atmosphere CPU, allocation and sky-draw limits passed.
The largest per-pair sky p95/p99 were 0.359520/1.005760 ms; the largest combined
CPU p99 was 0.0316 ms, with zero allocations and one sky draw in the on variant.

Strong between-run variation is observed, but its cause is unproven. GPU clocks,
power state or external scheduling are hypotheses, not established explanations.
No substantiated renderer/timestamp/canonical-comparison defect was found that
would justify changing these results. Keep this failure open, retain every row,
and collect paired diagnostic clock/scheduling evidence if the renewed reference
run repeats the failures. Do not remove outliers, weaken thresholds, or accept
only the pooled rows.

## Orbit combined — BLOCKED

The first off flight failed with `Orbit texture uploads did not settle before the
held measurement window.` Only one of 60 required attempts ran; no complete phase
or handoff sample set was produced. The logs retain the late texture activity and
additional native execution. This is not a measured sky-performance failure.

The fixed 40-frame warmup did not prove completion of all six production material
maps and staging retirement. The follow-up must prepare that exact composition
before the unchanged warmup and held window, with bounded failure, while retaining
the zero-upload/staging and strict native-execution checks inside the window.
A workstation functional smoke of a correction cannot qualify the reference limits.

### Corrective implementation and local verification

The follow-up now prepares all six exact current-cut material targets at their
requested mip and waits for empty staging before starting the existing warmup.
It uses the production barrier/retry/submit/presentation path, with a maximum of
208 acknowledged preparation frames and the unchanged process deadline. Failure
to establish readiness still aborts; sampling retains the upload-constancy guard,
including in smoke mode. No shader, material asset, package pin, numeric limit,
sample count or native-extra rejection was changed.

The local hardware Orbit combined smoke completed both off/on flights with zero
failed attempts and zero native extras. Each surface preparation needed **95
frames**, exceeding the old 40-frame warmup; all six targets were resident with
zero staging before sampling. Ascent and orbit were already ready. This is direct
functional evidence for the preparation correction, not a reference timing pass.
The smoke result remains `SMOKE ONLY`, `FullQualification=false`, and retains its
eight failed budget-summary rows. Artifacts: `artifacts/orbit-readiness-smoke-1/`.
The source was a dirty diagnostic working snapshot; the final commit and CI are
recorded in PR #83. Renew the reference run against the clean corrected head.

## Remaining acceptance

The [reference runbook](reference-qualification.md) and
[combined acceptance matrix](final-acceptance.md) remain authoritative.
Issues [Groundwork #112](https://github.com/DaveBeusing/Groundwork/issues/112) and
[WYU #42](https://github.com/DaveBeusing/wyu/issues/42) remain open.
Renew both real compositions on the corrected identified candidate. Keep this
archive as the failed baseline. Full driver-backed peak GPU memory, protected
work/pressure, published-package and installed-route evidence remain separate
open gates. `FullQualification=false` remains required.
