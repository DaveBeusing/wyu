# WP11-W04 procedural flight

Open a writable planet world and press **F6** from a verified ground pose.
The flight controller checks a landing footprint about five metres ahead.
For the curated route, use seed 42 and the initial heading. Takeoff may be
blocked by unsafe slope, missing coverage or an obstructed corridor. The
flight is automatic. **Esc** exits during flight and the last confirmed save
remains available. After touchdown, **F5** saves the landed state and
**Continue** revalidates the local contact against rebuilt collision.

The craft starts and lands at the same approved site. The controller
integrates bounded thrust at 60 Hz for ascent, injection, braking and
descent. The unpowered leg uses inverse-square gravity and velocity Verlet
integration at the generated planet's radius. The route completes at least
one unpowered revolution at roughly 160 m reference altitude before
returning to the departure azimuth. The desktop demo accelerates simulation
time. Flight does not move the ground avatar while it runs.

The near renderer uses the committed volumetric generation and its local
origin. At the flight clearance boundary, the renderer retires the near
resources and a separate graphics owner presents a coarse globe sampled
from the same recipe. The far marker uses the measured pad position. On
return, the near renderer binds a newer epoch and rebuilds the same terrain
revision and edit overlay before admitting the descent. The capsule sweep
sets contact position and rejects unsafe speed, slope or missing coverage.

The current recipe and flight envelope are deliberately bounded. The far
globe is a visual approximation of the baseline surface. Landing support,
including edits, is taken from the committed volume. Flight refuses edited
vertices outside the clearance envelope. Postflight saves keep the landed
record while the player remains near it and its terrain revision still
matches. Ordinary WP11 saves continue to load.

## Windows verification

Build in Release with the [verified package workflow](build-bootstrap.md).
From an interactive Windows desktop with Graphics Tools and NVIDIA, run
`./eng/Test-ProceduralFlight.ps1`. The script launches fresh WARP and NVIDIA
processes, edits a pad, compares semantic flight traces, saves at touchdown
and restarts with local coverage that omits the upper flight corridor.
It also aborts a WARP mission after the first far frame and
checks the preflight save in a fresh process.

The flight tests verify successful near/far/near presentation and renderer
retirement. The pinned
[Groundwork Windows CI run](https://github.com/DaveBeusing/Groundwork/actions/runs/43032545158)
provides the provider's WARP color and depth readback evidence, including the
volumetric transition and rebase. The WYU route checks Present and recovery
on the actual generated planet.

## Focused WARP readback

The diagnostic starts seed 42 with a fresh fixture, crosses both renderer
handoffs, and ends at the edited final approach. It reads RGBA8 and D32 from
the same retired WARP Present at each boundary, on orbit, and from the actual
approach eye aimed at the measured pad. The return probe captures the first
published Near content frame after the loading mask. The samples compare
projected globe geometry and colors, and check the edited pad against its
published mesh palette and collision hit. Each renderer uses its own
projection for depth verification.

Use `eng/Build-Wp11WarpReadbackProvider.ps1` to build the diagnostic provider
from Groundwork `49497e4`. Supply the Groundwork Git directory, verified
release package directory, pinned DXC and TerraFX archives, and a fresh
output directory. The build verifies stock shader provenance. Check out the
diagnostic changes in a WYU clone that contains the pinned `24c6c14` source
tree. Run `eng/Test-Wp11WarpPresentReadback.ps1` on an interactive Windows
desktop with Graphics Tools. Pass the provider output as `-ProviderBuild`
and a local NuGet cache containing the verified archives as `-PackageCache`.

The runner builds in a fresh directory from the pinned source plus the
diagnostic files. It preserves the original package cache and records both
the stock and loaded provider hashes. The output contains the raw GPU
attachments, per-Present JSON, assembly provenance, and a checksummed
`evidence.json` after all six probes and native shutdown pass.

Pass `-LandingMarkerOnly` to take one additional probe on the first Far
renderer and exit after it retires. The view keeps the ascent eye and aims
inside a marker triangle. Its GPU color and depth are checked against the
marker mesh, with the globe terrain behind it as a control on the same ray.
