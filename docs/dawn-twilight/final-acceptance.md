# Combined candidate acceptance

## Scope of this qualification

The [reference measurement policy](../reference-measurement-policy.md) keeps
reference-machine runs optional for merge and explicitly unqualified preview.
Required CI, reviews, functional and safety checks still apply. Record measured,
failed and unmeasured evidence against the exact candidate.

Use `GROUNDWORK_TWILIGHT_PREVIEW=1` to enable Twilight in both WYU and the
paired renderer. The normal launch and Continue paths keep the baseline
atmosphere, lighting, weather presentation and flight. The preview keeps seed,
accepted tick, terrain and save format. Approve the paired binaries for
functional evaluation separately from any release or production activation.

## Integrated inputs

PR #83 integrates WYU master `b7ae63069d4b6898bf8e3a1ef8ca3e6d2fa11095`,
including static construction #90, terrain edit feedback #98 and console status #99.
Groundwork #193 integrates the complete #186 head
`b40c11b14e64bff9b446d6eab18e8755916f66ec`: owner wake, presentation waiting,
acknowledged-frame arbitration and texture-refinement fairness. It also
integrates static construction through Groundwork master
`751a1e022d703c6dfd582c6521cd1cc4f9c6a2eb`.

In the preview, the shared sun direction drives sky, weather and terrain lighting. Orbit retains
its celestial frame; the approved dawn/dusk colour curve replaces its former
additional warm multiplier. PBR textures, boulders, fallback, prop warm-up and
save/profile metadata remain present. TW1–TW3 remain approved. Weather density,
the accepted clock and save payloads are not migrated.

All ten WYU engine modules use the immutable baseline from WYU master,
`0.1.0-dev.45079399733.1`, source
`7c889ca56ff9ae3dd6c712467e933221a254334f`, manifest SHA-256
`7B907182976531A1A7105CDEC6C0742C38C044FA0DF030F02F026F1C54004B2D`.
An unqualified paired preview uses separately staged `Groundwork.Rendering.dll`
and `Groundwork.Rendering.D3D12.dll` from #193 for diagnostics. Startup verifies
the supported Preview ABI stamp in both assemblies before starting a session.
Record client and renderer hashes with the evidence. Normal package restores,
installed R09 measurement and release provenance checks use their established paths.

The Preview handoff smoke checks the raw D3D12 live-object total against the
named retained session queue and the provider's classified Preview inventory.
Direct can retain two objects. Lookup can retain three, plus its parked texture
and heap with reuse enabled. Unexpected objects or incomplete native diagnostics
fail the smoke. Final shutdown requires zero live child objects.

The X11D run started at 22:28 on 22 November pins Groundwork `d67d8d6` and
WYU `0b194e3` with the prior immutable baseline `0.1.0-dev.45005277801.1`.
Its results apply to those inputs.
The archived 09:31 and 11:58 runs used baseline `0.1.0-dev.44510519303.1`
with both rendering DLLs staged together as a diagnostic overlay.
Preserve the master-only producer, archive and locked-restore checks when
qualifying the integrated combination.

## Required production qualification evidence

Prior captures and timing observations apply to their original heads. They do
not automatically qualify the merged combination. Final PR descriptions and
the measurement ZIP identify the delivered and tested source snapshots.

| Check | Required disposition |
| --- | --- |
| Gameplay | Preset solar override, fresh-cache parity and unchanged fog density, plus clock/night/allocation regressions. |
| Renderer | Admission/waiter cancellation, arbitration, sky/twilight, refinement, WARP/hardware handoff and window lifecycle coverage. |
| Functional route | Renew Far Horizon and Orbit flight/Continue, rebase, shared sunlight and native shutdown with the paired renderer. |
| Visual review | Renew sunrise/sunset captures for both profiles. Orbit samples its actual solar plane. Captures alone do not establish detailed night navigation or frame-by-frame animation acceptance. |
| Reference timing | **CHANGES REQUIRED.** The [23 November Direct and Lookup series](reference-series-2026-11-23.md) completed 240 WYU flights on Groundwork 8943e96 and WYU bd59baa. Direct fails 9 Far Horizon and 17 Orbit Combined rows. Lookup fails 3 Far Horizon and 8 Orbit Combined rows. The series report records the earlier evidence lineage and the paired Orbit Combined follow-up. |
| Full memory | **BLOCKED** until incremental peak GPU memory includes driver backing and overlapping handoff resources. A resource ledger alone cannot close the 4 MiB gate. |
| Protected work | **BLOCKED** until movement/edit/recovery and speculative CPU/GPU pressure limits cover the exact installed composition. |
| Published installation | **BLOCKED** until required approval, engine master CI/archive, coordinated consumer pins/locks/digests and installed-route checks are complete. |

## Reference execution

Use the [reference runbook](reference-qualification.md). The standalone runner
pins reviewed source heads and performs these serially:

1. Groundwork synthetic sky fixture: five pairs, 2,000 retained samples per
   variant/pair/phase, 60,000 rows, 64 warmups.
2. WYU `far-horizon`: five pairs, 400 retained samples per phase/variant/pair
   (2,000 pooled), six flights per variant/pair, 60 attempts.
3. WYU `orbit-combined`: the same counts with PBR, boulders, sunlight/shadows
   and props enabled equally in both sky variants.

Only submitted sky is removed in the off arm. Canonical camera, celestial,
lighting, profile and composition identities must match. Failed attempts remain
included. Actual successful native-Present sequences do not prove scanout.
The ZIP retains raw phase/handoff/preparation rows, summaries, all logs, source
and binary hashes, machine/driver details, the executed runner and failures.
`FullQualification=false` remains intentional while separate gates are open.

## Publication after qualification

The package workflow requires a successful producer **push on master**. Do not
repin to a PR artifact or weaken that guard. After required evidence and code
approval, perform the authorized engine merge, green master CI and immutable
archive, then update all ten modules, affected lockfiles and provenance digests
together. Renew installed checks and final reviews/CI for those exact bytes.
Source integration does not bypass acceptance gates or authorize a release.
