# Worlds Yet Unseen (WYU)

Worlds Yet Unseen is a first-person planetary survival and exploration game.
It uses a separate, independently packaged Groundwork engine.

The game is planned around procedurally generated spherical, destructible
voxel worlds, seamless surface-to-orbit travel and modular construction.
Initial platform is Windows; initial gameplay is single-player.

## Windows host

The host opens a Windows window, captures input, advances Groundwork's
fixed-step simulation, and shuts down when the window closes. The current
milestone exercises the engine lifecycle and uses an empty world. Pass
`--d3d12` to open the Windows host with the WP02 D3D12 cube renderer, or `--terrain` for the WP03 two-chunk terrain scene.
See the [build and smoke instructions](docs/build-bootstrap.md) to run it
from the independently archived Groundwork packages.
