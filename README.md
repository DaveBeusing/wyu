# Worlds Yet Unseen (WYU)

Worlds Yet Unseen is a first-person planetary survival and exploration game.
It uses a separate, independently packaged Groundwork engine.

The game is planned around procedurally generated spherical, destructible
voxel worlds, seamless surface-to-orbit travel and modular construction.
Initial platform is Windows; initial gameplay is single-player.

## WP01 bootstrap

The current host project is only a build scaffold. It explicitly reports
that game-host wiring is not implemented; it is not a playable milestone.
See docs/build-bootstrap.md for .NET 10 build requirements and the
security-gated local NuGet package handover. Engine source remains in
Groundwork, never linked directly into this repository.
