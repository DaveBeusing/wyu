# WP11 R09 installed WYU measurement

Use the self-contained Windows x64 R09 installer on the reference machine for [Groundwork #88](https://github.com/DaveBeusing/Groundwork/issues/88). The R09 installer uses a separate per-user application ID and directory. The runner exercises the installed binary in temporary world slots.

`eng/Measure-WyuR09.ps1` requires the independently supplied SHA-256 of the installer manifest, checks the installed files, and runs the Normal and Retry cohorts. Five measured repetitions per cohort follow one warm-up each. Keep the Windows desktop interactive and the NVIDIA adapter selected. Record changes to drivers, power or display settings with the evidence.

One speculative job occupies a worker and another fills the speculative queue in each measured route. The scripted fixture starts bounded private work for movement and the first sealed edit ahead of graphics startup and initial terrain loading. The movement forecast uses the ordinary support halo. The publishing worker selects the full flight halo and can reuse compatible bricks even where it has to build meshes at other LODs. At publishing dispatch, it offers only a source whose completion was observed by the original route start. The publishing worker can use that source after checking compatibility and edit invalidation. It falls back to a full build if the source was missing, late or rejected. Recovery retains the sequential private build within its route. The native upload hold starts after publishing work finishes. The client checks that the previous generation remains committed during the hold. The runner validates the admission, source and upload ordering, then matches the publishing generation to the authorized frame and its consuming `BeginTick`.

## Read the profiling results

Evidence schema `wyu-r09-installed-flight-v9` preserves the raw QPC endpoints in `observations.csv` and reports p50, p95, p99 and maximum in `summary.csv`. Compare `worker` (the publishing job) with `prebuild-worker` (the offered private source). Each measured worker has a queue interval before `Execute`, a completion observation interval after `Execute`, and six contiguous phases inside `Execute`. A movement or first-edit sample with no offered private source has no private worker phases or inventory. Publishing, route, upload, receipt and cold-fallback evidence is present for every run. Check the reported sample count before comparing private-phase percentiles.

| Phase suffix | Work measured |
| --- | --- |
| `overlay` | Edit overlay preparation and request refinement |
| `bricks` | Brick-store work, including density and material sampling |
| `plan` | Mesh plan creation and lease acquisition |
| `mesh` | Mesh construction and packaging of generated artifacts |
| `evidence` | Capture of diagnostic fingerprints from the completed brick and mesh outputs |
| `handoff` | Return of the completed result to the streaming coordinator |

### Brick stages and repeated work

- `brick-timings.csv` breaks each role's brick interval into reservation, allocation, density sampling, material sampling, shared-vertex validation, publication and residual time. Each stage is summed across that worker's bricks, with the original QPC frequency and outer brick interval in the same row. Residual time covers the rest of the outer interval, including orchestration and timing overhead. The diagnostic sampler groups density and material calls by row to avoid timing every sample individually. Measured sampling detours and fingerprint collection contribute to the full worker time. The `evidence` interval lets you report fingerprint cost separately when comparing runs.
- `brick-inventory.csv` lists signed brick keys, requested strides, SHA-256 fingerprints for density and material arrays, individual generation or reuse time, and a `Reused` flag. `brick-overlap.csv` counts shared brick keys, keys present in one role, and equality of payloads and requested stride as separate dimensions. `PrivateSamePayloadMilliseconds` sums the private worker's generation time for keys with matching density and material fingerprints. Requested stride describes the mesh request and is reported separately from brick content.
- `mesh-inventory.csv` lists the lattice origin, LOD mask, triangle count, SHA-256 fingerprint of emitted geometry, individual build or reuse time, and a `Reused` flag. `mesh-overlap.csv` compares origins, LOD masks and geometry, including triangle counts. `PrivateSameLodAndGeometryMilliseconds` sums the private worker's build time for origins with matching LOD and geometry.
- `reuse.csv` identifies the offered private generation, the publishing generation that produced the receipt and the source generation actually selected by the worker. It counts imported and rebuilt outputs for bricks and meshes. The runner cross-checks every reuse flag against the source's key and payload or origin, LOD mask, triangle count and geometry. For movement and first edit, `EarlyStatus` is `Ready`, `Pending`, `Rejected` or `Unavailable`. The row includes the prebuild attempt and decision QPC ticks, the ID of any admitted private generation, the lead from attempt to route setup, and the duration from observed readiness to decision when a source was offered. `ColdFallback` indicates that the engine selected no private source. A source can be ready and still be rejected at the point of reuse.

Every table preserves the path, cohort, run and warm-up flag. Compare roles within the same path and run. Brick keys index bricks and mesh origins are in lattice coordinates.

The Recovery private worker can import immutable bricks from the current committed terrain when the sampler, revision and overlay match. It keeps its full resource reservation. Missing bricks and unavailable sources use the ordinary sampling path. The private worker builds its own mesh plan and meshes. `reuse.csv` records the published source as `PrivateBrickSourceGeneration` and the private counts as `ReusedPrivateBricks` and `RebuiltPrivateBricks`. The existing publishing counts describe the later transfer from the private build. Run `-RequireRecoveryColdFallback` into a separate directory to exercise ordinary sampling in the same installed build. The runner requires a zero source and zero private hits for that run, labeled `forced-cold` in `machine.json`.

`held-upload` starts when the host observes the renderer entering the gate and ends at the actual release. `held-upload-release-to-ready` starts at that release and ends when the host sees the staged generation ready. `upload-to-observed-ready` starts earlier, before native staging. The route begins at the same setup point as v5 and ends at the consuming `BeginTick`. Recovery's route includes its sequential private build. Movement and first edit use lead time during graphics startup and initial terrain loading. The client writes the worker inventories after the consuming `BeginTick`. The gate still checks native readiness after release with scheduler pressure and the previous generation retained. The phase maxima in `summary.csv` can come from different runs.

The proposed #88 ceilings still apply to publishing `worker`, applicable full-route timings and the two existing 50 ms intervals. The added worker phases and hold interval are diagnostic observations.

### Recovery with and without flight overlap

Run both modes into separate output directories. `-RecoveryMode Overlap` (the default) starts return terrain during the flight. It reports `setup-to-begin-tick` for the full wall interval, `flight-overlap-window` from preload start to the return boundary, and `handoff-to-begin-tick` from that boundary to the consuming tick. Flight and worker work run concurrently within the overlap window.

`-RecoveryMode AtHandoff` starts the actual Recovery admission and worker at the existing return boundary, with the flight simulation paused until the consuming tick. The interval `recovery-no-flight-to-begin-tick` includes Recovery setup, admission, renderer handoff, private and publishing work, upload and authorized presentation. Renderer and worker execution can overlap during handoff. Only this interval is compared with the proposed 4,000 ms ceiling. Both modes retain the worker, release-to-native-ready and authorized-receipt-to-BeginTick endpoints.

Both modes also report separate QPC intervals for the renderer switch.

- `far-renderer-stop` measures the awaited stop of the flight renderer.
- `ground-renderer-startup` measures ground renderer startup through observed readiness.
- `ground-renderer-switch` spans from the start of the flight renderer stop to observed ground renderer readiness. It includes the elapsed time between stop and startup.
- `private-build-renderer-overlap` is the intersection of the renderer switch and the private worker's `Execute` interval. Queue and completion observation time are outside the private worker interval. When there is no overlap, both endpoints are the switch start.

The runner checks the renderer endpoints and recomputes the intersection from the raw QPC ticks in every sample. These intervals add diagnostic measurements with no separate proposed ceilings.

### Combined-pressure resources

- `resource-samples.csv` records the held-upload and native-ready checkpoints under the occupied speculative worker and queue slot while the previous terrain generation is retained. It includes charged terrain CPU bytes, generation and reader counts, process working set and private bytes. OS peak working set and the coordinator's reservation peak are lifetime counters. `pressure.csv` retains the cumulative CPU/native/upload/retirement reservation peaks.
- `gpu-owners.csv` contains live, fence-retained and released counts and D3D12 backing bytes by renderer owner and checkpoint. Live and retained backing are disjoint. These aligned tracked allocations measure resource backing. Driver residency, descriptor heaps and command allocator storage are outside this accounting.
- `combined-peaks.csv` reports the observed maximum across the two checkpoints per route and run. Each GPU total sums live and retained bytes within the same checkpoint. Recovery creates a fresh near renderer, so retained CPU terrain can coexist with zero old terrain backing at the held checkpoint.
- The shutdown record validates zero charged terrain, generations, readers, scheduler work and live or retained tracked GPU allocations. It reports the final process peak separately. Check the raw logs for D3D12 shutdown evidence.

For a deterministic fallback smoke, add `-RequireEarlyColdFallback` to the same installed runner command and choose a separate output directory. The fixture holds the early source pending until the publishing path makes its decision. Both early paths must then publish with zero reuse hits and settle cleanly. `machine.json` records `EarlyPrefetchMode` as `forced-pending` for this run. Use the default runner mode for the reference measurements.

## Rebuild

Use .NET SDK 10.0.403, the reviewed Inno Setup 6.7.3 compiler, and the ten Groundwork packages built locally from the pinned #88 reuse commit. The engine work builds on the revision verified by [Windows CI run 43945439532](https://github.com/DaveBeusing/Groundwork/actions/runs/43945439532). Take the exact engine commit, package version and manifest SHA-256 from the build handoff. Supply the original Groundwork `LICENSE` alongside the packages. Local host retry policy is carried forward from the previous R09 qualification candidate.

Run the existing verified consumer from a clean checkout with the following options. The builder records the exact WYU SHA and all payload hashes.

```powershell
./eng/Test-VerifiedGroundworkConsumer.ps1 -ArtifactDirectory $packages `
    -ExpectedManifestSha256 $engineManifestHash `
    -ExpectedSourceCommit $engineCommit `
    -ExpectedVersion $engineVersion `
    -InstallerCompiler $iscc -InstallerOutputDirectory $output -R09MeasurementInstaller
```

The native upload gate is enabled only for the opt-in scripted R09 flight and is bound to the pinned Groundwork package. Its eight-second watchdog and cleanup ensure a failed measurement releases the renderer. Keep the source bundle, installer and package manifests, measurement script and resulting ZIP together for review.
