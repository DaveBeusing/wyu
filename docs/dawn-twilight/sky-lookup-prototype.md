# Atmosphere lookup diagnostic prototype

**Diagnostic only.** The product default remains direct integration. The owner approved developing a prototype, not adopting interpolation error or changing any acceptance limit.

## Current implementation

Two fixed 256 × 128 RGBA32F fields store radiance and transmission for the current camera rays. Every sky frame regenerates them using the same physical model, eight view samples and four Gauss–Legendre solar samples. Camera, FOV, aspect, altitude, profile and sun changes use that frame's constants; there is no acceptance-only cache warming.

Ground and positive ground-clipped atmospheric intervals use the direct model's stable sphere logic. Neighboring geometric class changes and a mismatch with the current full-resolution ray activate the original direct integrator at the boundary. The sun disk, stars and occlusion remain evaluated at full resolution. This avoids the confirmed ground-horizon blur and outer-limb stepping of earlier prototype variants.

The implementation retains one sky draw, no terrain/mesh uploads and no additional resolution-dependent target dimensions. Actual committed texture allocation is 1,048,576 bytes per owner; two simultaneous owners were tested. **This is not complete 4 MiB memory acceptance:** descriptor, PSO, driver backing, staging/retirement overlap and protected pressure evidence remain separate.

Resources and PSOs are owner-local in the experimental path. Default direct-mode immutable session reuse is retained. Prototype cold setup and owner retirement remain charged; no handoff pass is inferred from functional tests.

## Exact work elimination

The delivered shader also skips the ground-sphere solve when the ray points outward (`dot(eye, ray) >= 0`). The existing stable solver would produce a negative minimum root and therefore the same miss. Native verification compares 212 before/after payload pairs across WARP and NVIDIA, including ten additional sun-radius/FOV/behind-sun cases: all are SHA-identical. Default captures are included.

More aggressive sun/star rearrangements were rejected after NVIDIA produced one-channel-step differences. Their source, binary, DXIL and failed report remain archived separately; no tolerance was relaxed to adopt them.

## Timing and evidence boundaries

The prototype uses the existing total sky and frame timestamps. One additional marker divides the sky interval:

- The first component includes the initial depth transition, required preparation and compute dispatch. It is not an isolated compute-shader time.
- The second includes UAV/SRV transitions, the full-resolution pixel draw, direct boundary fallback, restore barriers and depth restoration.

CPU components use the same original recording interval. WYU joins GPU and CPU components by owner, fence, sequence and phase, rejects missing, duplicate, non-finite or negative data, and requires the sum to match the original total within 0.000001 ms of floating-point rounding. The original total CPU/GPU/handoff limits remain authoritative. G-off must report no lookup split work.

All callback records use the existing bounded 8,192-entry value buffer, with no warmed per-event allocation or console output. Serialization happens after the controlled phases. Buffer overflow fails the measurement; no samples are selected or overwritten.

This native compute subroute is encapsulated inside Sky and is not a fully modeled compute/UAV dependency in the generic validated render plan. Production adoption requires explicit architectural assessment of that boundary.

## Safeguards

- Lookup is disabled by default. The standalone kit requires explicit `-DiagnosticProbe -LookupPrototype`.
- WYU positively verifies the requested device mode; legacy providers lacking the capability are permitted only for direct mode. Every native owner must report a consistent boolean identity.
- Lookup Smoke also enables diagnostics automatically. Lookup outside Smoke/DiagnosticProbe is rejected.
- The ordinary engine qualification runner rejects the prototype, and its native fixture explicitly selects direct integration.
- Child results must retain experimental identity and cannot be promoted to measured acceptance by the parent runner.
- No power, clock, display or driver settings change. No artificial GPU padding or altered numeric gates are used. Original acceptance denominators are unchanged; reduced diagnostic counts remain explicit and non-qualifying.
- All ten immutable consumer package pins remain `0.1.0-dev.44510519303.1`. Controlled replacement of both rendering DLLs is diagnostic provenance, not published-package approval.

## Image and motion evidence

The native NVIDIA matrix contains 12 static cases and five half-pixel camera samples each for ground, orbit and far orbit: 27 cases in total, at 1920 × 1080. Raw RGBA8, direct/lookup/difference BMPs, parameter and shader manifests, actual GPU fallback masks, and signed temporal-difference metrics are retained. A test-only idle-checked flag exports the fallback mask in a separate frame; RGB must match the ordinary frame exactly. Production alpha remains one.

The inspected geometric fallback pixels are bit-identical to the direct oracle. Numeric interpolation differences outside that region are reported, not approved. Static or sampled motion screenshots do not establish every continuous animation, Save/Continue, installed game journey or display configuration.

PNG counterparts are lossless exports with decoded-pixel hashes checked against the BMPs. The local `review.html` allows direct/lookup comparison, difference inspection and native-pixel viewing.

## Reference result and remaining acceptance

The [12:15 reference comparison](reference-probe-2026-11-21-121552.md) remains the authoritative latest reference-machine result: both handoff directions pass in that short probe, while all three relative GPU frame comparisons fail. Six summary rows are three comparisons repeated as pair and pooled, not six independent trials.

The full original protocol still requires five alternating pairs and all original sample counts, +5%/+8% frame GPU, 0.8/1.2 ms sky, 0.10 ms CPU, zero warmed allocations, one draw, +10% handoff and complete memory/pressure/published-package evidence. A two-flight diagnostic probe cannot establish acceptance, even if its numeric rows pass.

Final local measurements and exact source/CI identities are recorded below when available. No master merge, package publication or default adoption is authorized by this document.

## Bound implementation and verification

The benchmark kit freezes Groundwork `985eaa25c3be0b682fd4c04df664146f46d4c62a` and WYU runtime `a966427033097758d1aca47d6520ecddf6ab6584`. Later WYU report-only commits do not change these measured runtime inputs.

- Release builds: zero warnings/errors; 17 shader containers validated.
- Focused native regressions: WARP 10/10 and NVIDIA 4/4, zero skips.
- All 27 fallback masks report zero differences from the direct oracle at fallback pixels. Mask-frame RGB is identical to normal capture.
- Representative RGBA8 max/RMSE: surface dusk 2/0.07399, orbit limb 4/0.11492, far-64R limb 2/0.06978. These values are observations, not an approved tolerance.
- Five half-pixel camera samples per ground/orbit/far route retain signed temporal changes; maximum differences from the direct temporal change are 2/4/2 channel steps. This sampled evidence does not prove every continuous journey.
- Independent code/data reviewer: `integration`; security: `security_review`; rendered screenshot/operator review: `planning_uiux`. Confirmed prototype horizon blur and outer-limb stepping were corrected and re-reviewed. No final owner visual approval or complete qualification is implied.
- Evidence roots in Groundwork: `artifacts/sky-lookup-nvidia-v5-captures/f552a45edfb646d79a2d77bd4f67bfc2/`, `artifacts/sky-ground-cull-comparison.json`, `artifacts/rejected-stars-sun-comparison.json`, and native test TRX directories.
- The positive mode-binding, cancellation, zero-allocation buffer and strict component-join tests run in WYU CI. Standalone kit failure/ZIP/mode/promotion guards are tested separately.

## Final workstation probe: CHANGES REQUIRED

The frozen engine source passes [Windows CI](https://github.com/DaveBeusing/Groundwork/actions/runs/44908176936):
793 solution tests, zero failures/skips, including 105 renderer tests, plus package,
tamper and isolated-consumer gates. Tested merge:
`15c476f926ea80d475866132e06ccb1441e31271`, into master
`8317a841f6b24bfd1c2ca84d1893a750add9e4b2`.
The frozen WYU runtime passes [verified-consumer CI](https://github.com/DaveBeusing/wyu/actions/runs/44903776555),
including the new mode/split/failure regressions. Tested merge:
`a5f37948ea8ed6940997c35e1987ac3c5ac276c7`, into master
`29a42542e2ab5ac7b4d62fde07fd56e2044aea4e`. These CI results do not replace
reference performance or owner approval. Report-only WYU changes are checked
again in their own CI run.

The clean runtime commits above complete two native 1080p flights, 400 samples per phase and variant: 2,400 samples, zero failed attempts, zero native extras and zero component-sum mismatches. Evidence is in WYU `artifacts/sky-lookup-final-probe/` and `artifacts/lookup-final-integrated-build-inputs.json`.

| Phase | Frame p95 / p99 increase | Sky p99 | Compute segment median | Draw/barrier segment median |
| --- | ---: | ---: | ---: | ---: |
| Surface | +75.751% / +76.129% | 0.146240 ms | 0.004992 ms | 0.140128 ms |
| Ascent | +443.395% / +450.031% | 0.460864 ms | 0.006720 ms | 0.431904 ms |
| Orbit | +74.852% / +77.602% | 0.237696 ms | 0.005408 ms | 0.203584 ms |

CPU preparation plus recording p99 is at most 0.0589 ms with zero measured allocations. Handoffs are 51.0509/46.2872 ms off/on to orbit (-9.331%) and 21.0954/15.2922 ms returning (-27.509%). Both pass in this one-observation probe; this is not the full handoff distribution.

**All three relative GPU frame comparisons still fail.** Absolute sky and CPU limits passing does not override those failures. The six failed summary rows remain three comparisons duplicated as pair and pooled. No performance fix or source-to-source speedup is inferred.

This machine has an RTX PRO 5000 Blackwell laptop GPU, not the designated RTX 5070. Sampled telemetry reports P8/405 MHz memory in both variants; one-second samples cannot establish clocks during each sub-millisecond workload. These workstation percentages must not be treated as a comparison with, or acceptance result for, the reference-machine probe.

The draw/barrier segment remains dominant. Its endpoints do not distinguish individual barrier, ALU, sampling and output costs. Reducing the approximately 5–7 microsecond generation segment would not resolve the demonstrated gap and is not used to justify fewer integration samples.

## Next required evidence

Run the frozen diagnostic kit on the designated i9-10850K/RTX 5070:

```powershell
pwsh -NoProfile -File "\\X11D\Share\Invoke-GroundworkReferenceMeasurements.ps1" -DiagnosticProbe -LookupPrototype
```

The script stores results, all errors, ZIP and adjacent SHA256 beside itself. It stages both rendering DLLs from the pinned engine source and records their hashes. The same script with only `-DiagnosticProbe` remains a direct-model control.

Kit SHA256: `F90FCD710C378F00F5B8CF3BE052FDBB5CD1F5BDA9F9D00E7BD01D4059B277B5`. The matching offline record passes 12 real-process/function cases and 13 orchestration flows, including interrupted compositions, explicit prototype scope, missing/wrong mode, rejection of promoted acceptance, ZIP hashing and publication failure.

No further safe correction that closes the original GPU limits is established by the current evidence. New target-hardware split data or dedicated profiling is needed before selecting another optimization. Owner visual approval, full driver-backed memory, protected pressure, original complete-pair qualification and the approved published-package/installed route remain open. The PRs stay draft and unmerged.
