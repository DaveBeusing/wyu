# Combined candidate acceptance

## Integrated inputs

PR #83 integrates WYU master `904489845830a1a73b1527aca6138df3e441906d`,
including static construction #90 and console changes through #92.
Groundwork #193 integrates the complete #186 head
`b40c11b14e64bff9b446d6eab18e8755916f66ec`: owner wake, presentation waiting,
acknowledged-frame arbitration and texture-refinement fairness. It also
integrates static construction through Groundwork master
`751a1e022d703c6dfd582c6521cd1cc4f9c6a2eb`.

The shared sun direction drives sky, weather and terrain lighting. Orbit retains
its celestial frame; the approved dawn/dusk colour curve replaces its former
additional warm multiplier. PBR textures, boulders, fallback, prop warm-up and
save/profile metadata remain present. TW1–TW3 remain approved. Weather density,
the accepted clock and save payloads are not migrated.

All ten WYU engine modules use the immutable baseline approved in WYU
#90, `0.1.0-dev.45005277801.1`, source
`751a1e022d703c6dfd582c6521cd1cc4f9c6a2eb`, manifest SHA-256
`8E76DE5FE00E38755AC46F68693CEE75164078AC919B321E50ABBDB37FFA63C6`.
The archived 09:31 and 11:58 runs used baseline `0.1.0-dev.44510519303.1`
with both rendering DLLs staged together as a diagnostic overlay.
Preserve the master-only producer, archive and locked-restore checks when
qualifying the integrated combination.

## Required evidence

Prior captures and timing observations apply to their original heads. They do
not automatically qualify the merged combination. Final PR descriptions and
the measurement ZIP identify the delivered and tested source snapshots.

| Check | Required disposition |
| --- | --- |
| Gameplay | Preset solar override, fresh-cache parity and unchanged fog density, plus clock/night/allocation regressions. |
| Renderer | Admission/waiter cancellation, arbitration, sky/twilight, refinement, WARP/hardware handoff and window lifecycle coverage. |
| Functional route | Renew Far Horizon and Orbit flight/Continue, rebase, shared sunlight and native shutdown with the paired renderer. |
| Visual review | Renew sunrise/sunset captures for both profiles. Orbit samples its actual solar plane. Captures alone do not establish detailed night navigation or frame-by-frame animation acceptance. |
| Reference timing | **CHANGES REQUIRED.** The [09:31 Lookup reuse series](reference-series-2026-11-22-093150.md) completes all 120 flights. Far Horizon fails nine comparison rows and Orbit Combined fails seven. The [11:58 Far Horizon probe](reference-probe-2026-11-22-115849.md) still misses both Ascent frame gates. The [00:08 Orbit probe](reference-probe-2026-11-22-000819.md) remains short-run evidence. |
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
