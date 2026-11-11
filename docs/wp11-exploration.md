# WP11-W02 procedural exploration

Open **New World** in the alpha sidebar, or launch with `--new-world`.
Enter a decimal unsigned 64-bit seed, or leave the field blank to generate
one. The loading panel shows the recipe name, seed, generator version and
coverage status. Use **Continue** in the planet world selector to reopen a
committed checkpoint. See [terrain editing and saves](wp11-volume-save.md).
Use **F6** for the [procedural flight route](wp11-flight.md).

Use WASD to walk, the mouse to look, F1 for terrain and frame diagnostics,
and Esc to close the exploration window. The F1 panel shows the two brick
stride counts and current CPU and native-buffer reservations. Those numbers
are development diagnostics. Production budgets remain open.

The host streams a four-brick-wide window, checks the spawn against its
committed collision, then uses Groundwork's fixed 60 Hz movement step.
On a rebase it closes movement, presents a neutral mask, builds one bounded
terrain candidate, and publishes its prepared geometry through Core. Movement
reopens at a later BeginTick after D3D12 acknowledges the matching frame.
Camera frames use the same view and origin as the committed terrain.

## Windows route

Build in Release with the [verified package workflow](build-bootstrap.md),
then run the following from an interactive Windows desktop with Graphics
Tools installed.

```powershell
./eng/Test-VolumeExploration.ps1
```

The script runs seeds 42 and 20261111 in fresh WARP and NVIDIA processes.
Each run verifies spawn, scripted look rotation, minimization and restore,
480 fixed movement ticks, origin rebases, a terrain-window change, and
native window close. The route starts next to a 2:1 boundary and traverses
into its neighboring brick after the streamer promotes it to full collision
detail. The script compares the spawn and both route checkpoints across
adapters for each seed. Seed 20261111 uses the second spawn candidate.

For a manual check, start seed 42, let the loading panel clear and hold D
with the initial camera heading. The route is approximately 32 m along +Z.
Use F1 to watch the terrain change, move the mouse to turn, and minimize and
restore the window. A movement hold is reported in the sidebar if a
collision query exhausts its budget or coverage is unavailable.
