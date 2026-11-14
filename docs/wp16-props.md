# WP16 Far Horizon props

Far Horizon displays instanced rocks, scree, grass and trees. Set the environment variable `WYU_WP16=off` before launch to disable them.

Placement recipe 1 keys candidates by seed, signed dominant planet face, half-open 8 m cells and candidate ordinal. One bounded placement task reserves up to 128 nearby cells and eight candidates per cell, in distance order. It samples the published terrain lease for height and slope, reuses the existing biome rules, and settles its reservations after releasing the lease. Placement work issues no terrain requests. Prop residency is regenerated from committed terrain on load. A near renderer receives a fresh stream after a renderer handoff.

Rocks, scree and trees have a 128 m render cutoff and separate LOD enter and exit distances. Grass uses a 32 m cutoff and lower density in highlands. Placement availability depends on the active terrain window and the 128-cell admission cap. A single cancellable placement job runs independently of movement. New publications supersede pending work and the game stages completed results only for the current publication. Same-revision publications clear and resample props against the new collision geometry. After an edit, support outside the edit halo is reused only when the collision window and every brick stride match. A changed window or stride invalidates the old placements.

Run the guarded NVIDIA route after the verified consumer build with `pwsh -File eng/Test-ReferenceProps.ps1`. The smoke holds placement while 12 movement ticks commit, rejects reuse under shifted coverage or changed LOD, and checks native draws from mixed LODs, cell staging and retirement, and zero payload uploads during the camera sweep. It also validates the NVIDIA adapter, Debug Layer, DRED and native shutdown.

The full WP16 acceptance in WYU #44 includes save/reload parity, dedicated edit and hysteresis cases, the dense stress fixture and reference-class performance budgets. Those remain open.

## Workstation evidence

The Windows x64 Release route used Groundwork 0.1.0-dev.43590571564.1 from commit 8382269. Both seeds completed 480 movement ticks on the NVIDIA RTX PRO 5000 Blackwell Generation Laptop GPU (driver 32.0.15.9653) with Debug Layer and DRED at a 944 × 501 presented extent.

| Seed | Terrain views / windows | Peak prop draws / visible instances | Staged / retired cells |
| --- | ---: | ---: | ---: |
| 42 | 3 / 2 | 8 / 179 | 355 / 36 |
| 20261111 | 5 / 3 | 6 / 183 | 609 / 86 |

Both camera sweeps recorded zero mesh and instance payload bytes. Both shutdowns released native children and reported zero D3D12 errors or corruption. The diagnostic queue reported 492 and 498 warnings respectively. This was a functional route at the workstation window size.
