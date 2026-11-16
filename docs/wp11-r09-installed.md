# WP11 R09 installed WYU measurement

Use the self-contained Windows x64 R09 installer on the reference machine for [Groundwork #88](https://github.com/DaveBeusing/Groundwork/issues/88). The R09 installer uses a separate per-user application ID and directory. The runner exercises the installed binary in temporary world slots.

`eng/Measure-WyuR09.ps1` requires the independently supplied SHA-256 of the installer manifest, checks the installed files, and runs the Normal and Retry cohorts. Five measured repetitions per cohort follow one warm-up each. Keep the Windows desktop interactive and the NVIDIA adapter selected. Record changes to drivers, power or display settings with the evidence.

The publishing job uses the protected scheduler lane while one speculative job occupies a worker and another fills the speculative queue. During the native upload hold, an additional protected private build runs while the previous generation stays readable. The runner records the hold release and readiness of the staged generation, and validates the authorized frame and its later consuming `BeginTick` using the coordinator journal. It reports the three worker intervals separately from host admission and retries.

The Recovery wall interval starts before return terrain preload and includes overlapping flight. The return-to-ground handoff interval is reported separately. The runner leaves the 4,000 ms Recovery comparison for review. `pressure.csv` contains cumulative CPU, native, upload and retirement peaks in the terrain coordinator's accounting dimensions. These are reservation peaks. End-of-route native settlement is checked independently in the logs.

## Rebuild

Use .NET SDK 10.0.403, the reviewed Inno Setup 6.7.3 compiler, and the ten Groundwork packages from [Windows CI run 43945439532](https://github.com/DaveBeusing/Groundwork/actions/runs/43945439532). The expected Groundwork source is `bbf7ce1d781345b057d37d58d752b1b66589b6d0`, package version `0.1.0-dev.43945439532.1`, and package manifest SHA-256 `AF01485D5C20EC31A8C8E151F65FF2843E6FACE41853BD5ABC3F188DB0C2718F`. Supply the original Groundwork `LICENSE` alongside the packages. Local host retry policy is carried forward from the previous R09 qualification candidate.

Run the existing verified consumer from a clean checkout with the following options. The builder records the exact WYU SHA and all payload hashes.

```powershell
./eng/Test-VerifiedGroundworkConsumer.ps1 -ArtifactDirectory $packages `
    -ExpectedManifestSha256 'AF01485D5C20EC31A8C8E151F65FF2843E6FACE41853BD5ABC3F188DB0C2718F' `
    -ExpectedSourceCommit 'bbf7ce1d781345b057d37d58d752b1b66589b6d0' `
    -ExpectedVersion '0.1.0-dev.43945439532.1' `
    -InstallerCompiler $iscc -InstallerOutputDirectory $output -R09MeasurementInstaller
```

The native upload gate is enabled only for the opt-in scripted R09 flight and is bound to the pinned Groundwork package. Its eight-second watchdog and cleanup ensure a failed measurement releases the renderer. Keep the source bundle, installer and package manifests, measurement script and resulting ZIP together for review.
