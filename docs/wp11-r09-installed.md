# WP11 R09 installed WYU measurement

Use the self-contained Windows x64 R09 installer on the reference machine for [Groundwork #88](https://github.com/DaveBeusing/Groundwork/issues/88). The R09 installer uses a separate per-user application ID and directory. The runner exercises the installed binary in temporary world slots.

`eng/Measure-WyuR09.ps1` requires the independently supplied SHA-256 of the installer manifest, checks the installed files, and runs the Normal and Retry cohorts. Five measured repetitions per cohort follow one warm-up each. Keep the Windows desktop interactive and the NVIDIA adapter selected. Record changes to drivers, power or display settings with the evidence.

The publishing job uses the protected scheduler lane while one speculative job occupies a worker and another fills the speculative queue. During the native upload hold, an additional protected private build runs while the previous generation stays readable. The runner verifies that the private worker's full `Execute` interval falls within the observed native upload hold. It also validates the authorized frame and its later consuming `BeginTick` using the coordinator journal.

## Read the profiling results

Evidence schema `wyu-r09-installed-flight-v3` preserves the raw QPC endpoints in `observations.csv` and reports p50, p95, p99 and maximum in `summary.csv`. For each path and cohort, compare `worker` (the publishing job) with `overlap-worker` (the private job). Each has a scheduler queue interval before `Execute`, a completion observation interval after `Execute`, and five contiguous phases inside `Execute`.

| Phase suffix | Work measured |
| --- | --- |
| `overlay` | Edit overlay preparation and request refinement |
| `bricks` | Brick generation, including density sampling performed there |
| `plan` | Mesh plan creation and lease acquisition |
| `mesh` | Mesh construction and packaging of generated artifacts |
| `handoff` | Return of the completed result to the streaming coordinator |

`held-upload` starts when the host observes the renderer entering the gate and ends at the actual release. `held-upload-release-to-ready` starts at that release and ends when the host sees the staged generation ready. `upload-to-observed-ready` starts earlier, before native staging. Use the separate intervals to distinguish time spent waiting for the private build from native readiness after the release. Compute a phase's share from matching samples in `observations.csv`. The phase maxima in `summary.csv` can come from different runs.

The proposed #88 ceilings still apply to publishing `worker`, applicable full-route timings and the two existing 50 ms intervals. The added worker phases and hold interval are diagnostic observations.

The Recovery wall interval starts before return terrain preload and includes overlapping flight. The return-to-ground handoff interval is reported separately. The runner leaves the 4,000 ms Recovery comparison for review. `pressure.csv` contains cumulative CPU, native, upload and retirement peaks in the terrain coordinator's accounting dimensions. These are reservation peaks. End-of-route native settlement is checked independently in the logs.

## Rebuild

Use .NET SDK 10.0.403, the reviewed Inno Setup 6.7.3 compiler, and the ten Groundwork packages built locally from the pinned #88 profiling commit. The engine work builds on the revision verified by [Windows CI run 43945439532](https://github.com/DaveBeusing/Groundwork/actions/runs/43945439532). Take the exact profiling commit, package version and manifest SHA-256 from the build handoff. Supply the original Groundwork `LICENSE` alongside the packages. Local host retry policy is carried forward from the previous R09 qualification candidate.

Run the existing verified consumer from a clean checkout with the following options. The builder records the exact WYU SHA and all payload hashes.

```powershell
./eng/Test-VerifiedGroundworkConsumer.ps1 -ArtifactDirectory $packages `
    -ExpectedManifestSha256 $engineManifestHash `
    -ExpectedSourceCommit $engineCommit `
    -ExpectedVersion $engineVersion `
    -InstallerCompiler $iscc -InstallerOutputDirectory $output -R09MeasurementInstaller
```

The native upload gate is enabled only for the opt-in scripted R09 flight and is bound to the pinned Groundwork package. Its eight-second watchdog and cleanup ensure a failed measurement releases the renderer. Keep the source bundle, installer and package manifests, measurement script and resulting ZIP together for review.
