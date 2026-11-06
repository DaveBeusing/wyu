# Worlds Yet Unseen (WYU)

Worlds Yet Unseen is a first-person planetary survival and exploration game.
It uses a separate, independently packaged Groundwork engine.

The game is planned around procedurally generated spherical, destructible
voxel worlds, seamless surface-to-orbit travel and modular construction.
Initial platform is Windows; initial gameplay is single-player.

## Windows host

The host opens a Windows window, captures input, advances Groundwork's
fixed-step simulation, and shuts down when the window closes. Pass `--d3d12`
to open the WP02 D3D12 cube, or `--terrain` to open the WP04 editable two-chunk
terrain scene. In terrain mode, press **T** to remove or restore the voxel at
the shared chunk boundary.
See the [build and smoke instructions](docs/build-bootstrap.md) to run it
from the independently archived Groundwork packages.
