# WP11 R09 installed WYU measurement

Use the self-contained Windows x64 R09 installer on the reference machine for [Groundwork #88](https://github.com/DaveBeusing/Groundwork/issues/88). The R09 installer uses a separate per-user application ID and directory. The runner exercises the installed binary in temporary world slots.

`eng/Measure-WyuR09.ps1` requires the independently supplied SHA-256 of the installer manifest, checks the installed files, and runs the Normal and Retry cohorts. Five measured repetitions per cohort follow one warm-up each. Keep the Windows desktop interactive and the NVIDIA adapter selected. Record changes to drivers, power or display settings with the evidence.

One speculative job occupies a worker and another fills the speculative queue in each measured route. The scripted fixture starts bounded private work for movement and the first sealed edit ahead of graphics startup and initial terrain loading. At publishing dispatch, it offers only a source whose completion was observed by the original route start. The publishing worker can use that source after checking compatibility and edit invalidation. It falls back to a full build if the source was missing, late or rejected. Recovery retains the sequential private build within its route. The native upload hold starts after publishing work finishes. The client checks that the previous generation remains committed during the hold. The runner validates the admission, source and upload ordering, then matches the publishing generation to the authorized frame and its consuming `BeginTick`.

## Read the profiling results

Evidence schema `wyu-r09-installed-flight-v6` preserves the raw QPC endpoints in `observations.csv` and reports p50, p95, p99 and maximum in `summary.csv`. Compare `worker` (the publishing job) with `prebuild-worker` (the offered private source). Each measured worker has a queue interval before `Execute`, a completion observation interval after `Execute`, and six contiguous phases inside `Execute`. A movement or first-edit sample with no offered private source has no private worker phases or inventory. Publishing, route, upload, receipt and cold-fallback evidence is present for every run. Check the reported sample count before comparing private-phase percentiles.

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

`held-upload` starts when the host observes the renderer entering the gate and ends at the actual release. `held-upload-release-to-ready` starts at that release and ends when the host sees the staged generation ready. `upload-to-observed-ready` starts earlier, before native staging. The route begins at the same setup point as v5 and ends at the consuming `BeginTick`. Recovery's route includes its sequential private build. Movement and first edit use lead time during graphics startup and initial terrain loading. The client writes the worker inventories after the consuming `BeginTick`. The gate still checks native readiness after release with scheduler pressure and the previous generation retained. The phase maxima in `summary.csv` can come from different runs.

The proposed #88 ceilings still apply to publishing `worker`, applicable full-route timings and the two existing 50 ms intervals. The added worker phases and hold interval are diagnostic observations.

The Recovery wall interval starts before return terrain preload and includes overlapping flight. The return-to-ground handoff interval is reported separately. The runner leaves the 4,000 ms Recovery comparison for review. `pressure.csv` contains cumulative CPU, native, upload and retirement peaks in the terrain coordinator's accounting dimensions. These are reservation peaks. End-of-route native settlement is checked independently in the logs.

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
