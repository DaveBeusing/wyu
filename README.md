# Worlds Yet Unseen

Worlds Yet Unseen (WYU) is a first-person planetary survival and exploration game
developed by **undefined behavior Studios**. The Windows single-player alpha
runs on [Groundwork Engine](https://github.com/DaveBeusing/Groundwork), a C# game
engine targeting .NET 10 with a Direct3D 12 backend.

[Architecture](#architecture) · [Windows alpha](#windows-alpha) · [Build and verification](#build-and-verification) · [Roadmap](#roadmap)

## Architecture

The [solution](WorldsYetUnseen.slnx) contains the game client, gameplay project,
and gameplay tests.

| Area | Responsibility |
| --- | --- |
| Groundwork Engine | Win32 windowing and input, fixed-step simulation, Direct3D 12 rendering, voxel terrain generation and streaming, terrain collision, and checkpoint storage |
| WYU client | Game input and menus, mission and world selection, save and continue flows, and integration of the engine modules |
| WYU gameplay | Rules and progression for gathering resources, assembling and activating the rescue beacon, and completing the mission |

WYU consumes exact versions of Groundwork NuGet packages from verified archives.
The build stages the verified packages and restores against checked-in NuGet
lockfiles. [Package handover and verification](eng/README.md)

## Windows alpha

Start the installed client from the Windows Start menu or run the client
without arguments. The sidebar includes these session entry points.

| Mode | What it exercises |
| --- | --- |
| **Start test mission** | Explore, mine stone, assemble and activate the rescue beacon, then fly from the planetary pad to orbit and return. **Continue** restores a supported checkpoint during construction or after landing. |
| **New World** | Open the standard planet world selector. |
| **Free Test World** | Open a seeded planet world or an isolated rescue mission. **New Test World** accepts seed 42 or a custom seed. **Continue** reopens a test slot. Test saves are separate from normal saves. |

| Input | Action |
| --- | --- |
| **W/A/S/D**, **mouse**, **E** | Move, aim, and interact |
| **Esc**, **arrow keys**, **Enter** | Toggle the sidebar and navigate its options in the rescue mission |
| **F1** | Toggle diagnostics. In Free Test World, this includes build, profile, seed, generator version, and position. |
| **F2** | Open setup actions in Free Test World |
| **F3** | Open or close the console while exploring a planet on foot |
| **F5**, **F10** | Save or save and exit in Free Test World |
| **F6** | Request flight on supported worlds, subject to the launch-site check |

In Free Test World, Ember Ridges and Ember Caves can grant ore through the
setup menu. **New Far Horizon World** exposes time and fog controls and includes
biomes, vegetation, sky, and terrain shadows.

In the world console, type a command and press **Enter**. **Tab** completes
commands and supported values, **Up/Down** recalls earlier input, and **Esc**
returns control to the game. **Ctrl+R** searches earlier commands. Press it
again for an older match, **Enter** to edit the match, or **Esc** to return to
your draft. Type **help** for the commands available in the
current world. **help <command>** shows usage, an example and the current value
where available. **help 2** opens the second command page. **status** shows the world, seed, generator, position and
presentation settings. **reset** restores the default field of view, live time
and automatic fog where available. Use **fov** to adjust the vertical field of view, **stats** to
toggle F1 diagnostics, and **time** and **fog** where the world supports weather.
Use **time live** to return to the world clock. Console overrides apply during
the current exploration session and are left out of the save.

**preset save <name>** stores the three presentation settings for this session.
Use **preset show <name>** to read them, **preset diff <a> <b>** to see their
differences, and **preset load <name>** to apply them. **preset list** shows
the names and **preset delete <name>** removes one. **inspect** shows the local
biome, altitude, and regional fog where available. **pos copy** copies the world,
seed, and coordinates to the Windows clipboard.

**preset copy <name>** copies a portable preset. Run **preset import** to
preview it from the clipboard, or enter **preset import <data>** with the copy
text. Use **preset import confirm** to store it under its original name, or
**preset import cancel** to discard the preview. Load it separately to apply
the imported settings. Fog and time overrides require a world that supports them.
**undo** restores the presentation before the last
console edit, including a reset or preset load.

**inspect aim** reports the terrain hit along the camera ray. **stream status**
shows active terrain LODs and streaming resource use. **mark save <name>**
stores the current location for this session. **mark list** lists saved names
and coordinates, and **mark show <name>** shows the full location details.

**profile 10** targets a ten-second sample of the on-foot view. The report gives
the actual sampled span. If the final poll is more than 250 ms late, the run
exits without a report. Close the console to
move during the measurement, then reopen it for the report. Use **profile** to
show the last completed result or **profile cancel** to stop a run. Observed
FPS counts at most one acknowledged sequence advance per poll. The report also
shows poll gaps and renderer replacement and skip counter deltas.

Use **profile keep a** or **profile keep b** to copy the last completed
measurement into that session's slot. A new measurement leaves the two slots
available. **profile compare** shows both measured durations and rates side by
side. The count-based metrics are shown per second, including poll gaps over
33 ms, so you can compare samples of different lengths.

**profile run <a> <b> [sec]** measures two saved presets from the same camera
position and direction. Keep the console open. Each preset has a one-second
warmup after its first presented frame, then a 1–30 second sample (5 seconds by
default). Presets with live time use the same lighting and weather tick for
comparison. A successful run fills both slots and restores your earlier settings.
**profile cancel** or closing the console restores your settings and preserves
the previous results.

**profile repeat <a> <b> [n]** runs 2–6 pairs (default 3) of five-second
samples, alternating which preset runs first. The comparison and copied report
include each preset's median and range of observed FPS. The report also includes
the settings and measurements from the last pair.

**report copy** copies the A/B comparison to the Windows clipboard. It includes
the client build and the world, seed, generator, position and presentation
settings recorded at the start of each measurement. Keep a profile in both A
and B before copying.

## Build and verification

Use Windows, the .NET 10 SDK, PowerShell 7, and GitHub CLI. The
[build and verification guide](docs/build-bootstrap.md) covers verified archive
setup, locked restore, Release builds, smoke modes, and the Windows installer.

- [Installed planet playtest](docs/wp11-installed-playtest.md)
- [Volumetric checkpoints and save recovery](docs/wp11-volume-save.md)
- [Underground geology and caves](docs/wp12-caves.md)

## Roadmap

- [Shared Groundwork and WYU roadmap](https://github.com/DaveBeusing/Groundwork/blob/master/docs/roadmap.md) tracks work packages and acceptance evidence across both repositories.
- [WYU roadmap](docs/Roadmap.html) is the game's roadmap in HTML.
- [World vision](docs/World_Vision_Consolidated.md) describes the longer-term game design.
