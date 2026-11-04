# WP01-L6 — verified Groundwork CI consumer (draft)

This change adds a Windows CI acceptance gate to WYU without linking any
Groundwork source project or requiring a shared worktree.

## Candidate producer record

- Repository: DaveBeusing/Groundwork
- GitHub Actions run: 41920122570, attempt 1, conclusion success
- Workflow: Groundwork Windows CI
- Producer PR head: 6930d66d383fb26427cf927115efe5cf9fc9b74b
- Candidate version: 0.1.0-dev.41920122570.1
- Manifest SHA-256: E07D23C78F922E8EBB193E03E5B7F5C2A8FDD4EF50C081B914B183D4017296F9

The CI workflow validates the immutable candidate build record, fetches its
artifact from GitHub Actions, compares the pinned manifest hash, checks the
manifest's source commit against the producer run head and then calls
eng/Test-VerifiedGroundworkConsumer.ps1.

The PowerShell script verifies all six artifact package names, sizes and
SHA-256 digests, then compares the **committed** WYU PackageReference and NuGet
lock against the **exact** candidate version. After these checks, it copies
only Groundwork.Core into a temporary external feed, clears the NuGet cache,
runs locked restore and a Windows Release build.

## Expected current result: blocked until pin migration

As of this draft, WYU **intentionally still pins**
0.1.0-dev.20261104215832.1 with a lock generated from a separately built local
package. This is different from the candidate CI artifact and **must not**
silently succeed. The workflow should fail closed at the version check.

An owner must select and approve the desired Groundwork package, preserve
its reproducible artifact before the 90-day CI-artifact retention expiry, and
regenerate/commit the genuine SDK-generated packages.lock.json from the
chosen verified bytes on Windows. Never hand-edit NuGet contentHash values.
A green workflow is not an implicit release approval. PR build provenance is
still not the same as a protected release attestation.

The current check does not establish actual Windows engine lifecycle behavior,
nor does it complete WYU L6 runtime composition. Recheck GitHub repository
access/permissions if cross-repository gh artifact download fails.
