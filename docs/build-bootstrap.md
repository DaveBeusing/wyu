# WYU Windows alpha build and verification

Build and run from a clean WYU checkout on Windows with the .NET 10 SDK,
PowerShell 7, and GitHub CLI. The engine is supplied by the immutable
Groundwork NuGet archive. WYU pins all ten packages at
`0.1.0-dev.43505500368.1` and commits the SDK-generated lockfile.
The renderer runs on Windows x64 and requires a D3D12 hardware adapter.

## Offline alpha installer rehearsal

The installer build requires .NET SDK `10.0.403` and ISCC from the
reviewed [Inno Setup 6.7.3 distribution](https://github.com/jrsoftware/issrc/releases/tag/is-6_7_3).
Verify the compiler download signature and SHA-256
`9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732`
before installing the build tool. The verified consumer gate below
accepts `-InstallerCompiler <path-to-ISCC.exe> -InstallerOutputDirectory <new-directory>`.
It builds from a clean checkout, uses the separate Windows x64 locks
and produces a self-contained setup executable and installer manifest.

Run the rehearsal on an interactive Windows 11 x64 desktop with an
unelevated account and a profile without an existing WYU Alpha install.
Supply the approved manifest hash independently of the installer folder.

```powershell
./eng/Test-OfflineInstaller.ps1 -InstallerDirectory $installerDirectory `
  -ExpectedManifestSha256 $approvedInstallerManifestHash
```

The script verifies installed hashes, starts the app from its Start menu
shortcut, checks that it loaded the bundled runtime, repairs a missing
program file by reinstalling, then uninstalls and checks test data hashes.
Enable the PlanetPlaytest switch for the [WP11 installed route](wp11-installed-playtest.md).
The WP10 alpha accepts an unsigned installer. The fresh Windows 11 offline
run remains an open follow-up in the Groundwork handover.

Download the archive to a new temporary directory and run the verification
gate from the WYU repository root.

```powershell
$archive = Join-Path ([IO.Path]::GetTempPath()) ('wyu-archive-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $archive | Out-Null
gh release download archive-groundwork-0.1.0-dev.43505500368.1-immutable `
  --repo DaveBeusing/Groundwork --dir $archive
if ($LASTEXITCODE -ne 0) { throw 'Groundwork archive download failed.' }
./eng/Test-VerifiedGroundworkConsumer.ps1 -ArtifactDirectory $archive `
  -ExpectedManifestSha256 EA858A18E300C9F452CDC3B0A93070A4CE8A9820D182BD81EAF79B086FDB2C9A `
  -ExpectedSourceCommit 695db49d4804aaacda38da123ca9d75951a65eb7 `
  -ExpectedVersion 0.1.0-dev.43505500368.1
```

The gate checks package provenance, stages a temporary external NuGet feed,
restores in locked mode with a fresh cache, builds in Release, and runs
headless startup/shutdown and controlled-fault smoke cases. Add `-WindowSmoke`
on an interactive Windows desktop to open and close a real window under the
same gate. Add `-D3D12Smoke` to verify hardware adapter selection, first
cube presentation and native window close. The D3D12 smoke requests Debug
Layer and DRED. Windows Graphics Tools enables the debug layer and live-object
report. Start the cube interactively with `--d3d12` and close the window to
stop. Starting without an argument opens the WP10 alpha sidebar.

Run `--planet` for the WP08 walking route. Use WASD to walk, left/right
arrows to turn and Esc to exit. The title bar points to the turnaround
at 145° and then back to the 35° starting point. It also shows whether
the route is waiting for terrain coverage. This mode uses a fresh session.
Add `-PlanetSmoke` to the verified consumer command to drive both legs
on NVIDIA with Debug Layer and DRED and verify native shutdown.

Run `--flight [slot-directory]` for the guided WP09 mission. Space starts
the flight and Esc closes the session. The default slot is the WP09
directory under local application data. The host saves the grounded craft
before departure and after touchdown and restores the landed checkpoint
on the next launch. In the near view the landing site is marked directly
on the terrain by an amber diamond with a dark outline. Add `-FlightSmoke`
to the verified consumer command to
fly the mission twice on NVIDIA with Debug Layer and DRED. Each run uses a
fresh slot and a separate reload process. The gate compares the mission
trace, landed pose and edit overlay across processes.

```powershell
dotnet ./artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll --d3d12
```

For archive provenance and CI verification, see [the package handover](../eng/README.md).

Launch with `--terrain` to view two adjacent voxel chunks. The camera shows the
shared boundary up close so you can see the edit. Press **T** to remove the
boundary voxel and press it again to restore the voxel. Escape or native window
close stops the host.

```powershell
dotnet ./artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll --terrain
```

Add `-TerrainSmoke` to the verified consumer command on an interactive Windows
desktop with Graphics Tools installed. The smoke exercises voxel removal and
restoration, rejection of stale geometry generations, native close and a clean
D3D12 live-object report. For WP04 qualification, check the adapter diagnostics
for NVIDIA hardware with an active Debug Layer and DRED.

## WP05 walkable region

Launch the WP05 region and use **W/S** to move forward or back, **A/D** to
strafe, and **Left/Right** to turn. Escape or native window close stops the
host.

```powershell
dotnet ./artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll --walkable
```

Add `-WalkableSmoke` to the verified consumer command on an interactive
Windows desktop with an NVIDIA D3D12 adapter and Windows Graphics Tools
installed. This runs `--walkable-smoke`, which enables Debug Layer and DRED
and drives the 914-tick fixed route from seed `0x57505535`, generator version
`1`. The route reports its checkpoints, waits for terrain presentation
at each chunk transition, checks wall contact and return, and closes the
native window. Each readiness hold has a 10-second watchdog, and the
host route smoke has a 120-second timeout. Check the adapter diagnostics
for NVIDIA hardware, active Debug Layer and DRED, and a clean live-object
report after shutdown.

See the [Groundwork WP05 route contract](https://github.com/DaveBeusing/Groundwork/blob/269ab9e1128c15f5764f0dded499e8e02db4110c/docs/architecture/wp05-walkable-region-contract.md)
for the fixed checkpoints and resource limits.

## WP06 save and resume

Launch the walkable region with save and resume enabled. The optional path
selects a save slot directory.

```powershell
dotnet ./artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll --save-world
```

Press **T** to toggle the boundary voxel in the test terrain and **F5** to save.
On restart, use the same slot directory to resume the last committed terrain,
player pose, and inventory.

Add `-SaveSmoke` to the verified consumer command to exercise interrupted
writes on Windows. The headless smoke kills the writer process after the
pending save has been flushed and checks that a fresh process can still
load the last committed terrain edit, player pose, and inventory.

## WP10 alpha entry

Launch the client without arguments. Use the arrow keys and Enter in the
left sidebar. Esc toggles the sidebar at startup and opens or closes
the pause menu during the rescue and flight. F1 toggles the performance
panel at the top left. It shows observed presentation FPS, GC collection
counts and heap size, and replaced or skipped frames. **Controls**
replays the introduction. Enter advances
each page and Esc skips it. In **Settings**, Left and Right edit the
selected value. Reset stages the defaults, Apply saves them, and Cancel
or Esc discards the draft.

Settings live in `%LOCALAPPDATA%\WorldsYetUnseen\Alpha\settings.json`.
An invalid file is kept when the client starts with defaults. Applying
settings saves the rejected file beside the new one for diagnosis.
Run `eng/Test-AlphaFirstRun.ps1 -Client <path-to-client-exe>` from an
interactive Windows desktop to exercise the menu with isolated data.

After activating the rescue beacon, press Enter to save the rescue
checkpoint and open the guided flight at the planetary pad. Press Space
to launch. Follow the amber pad on approach and wait for **Mission
complete** after touchdown. Continue returns to the saved construction
before the signal, or to the flight after the signal. Once touchdown is
saved, Continue restores the completed mission.

Run `eng/Test-AlphaMission.ps1 -Client <path-to-client-exe>` on an
interactive D3D12-capable Windows desktop. It seeds a partially built
beacon in an isolated slot, saves and resumes through the menu, completes
the guided orbit, and compares the saved and restored landing.

Startup and Direct3D 12 failures show a Windows dialog with the local
log location when the record was saved. Logs live under
`%LOCALAPPDATA%\WorldsYetUnseen\Logs`. WYU keeps the current and previous
log, up to 64 KiB each. Records include the build, process and mission or
loading phase. Closing the window during a save load cancels the load
and waits for the owned work to settle.

From an interactive Windows desktop, run
`eng/Test-AlphaDiagnostics.ps1 -Client <path-to-client-exe> -ExpectedCommit <build-commit>`
to check both dialogs, rotation and native close during a controlled load.

## WP07 rescue beacon

Launch the Windows client without arguments or use `--prototype`. The
window title displays the start menu, with **New Game** for an empty slot
or **Continue** for a valid checkpoint. Navigate with **Up/Down** and
**Enter**. During play, the title shows the objective, stone, deposit
count and the action in focus. Use **WASD** to move, the **mouse** to aim,
**E** to interact and **Esc** to pause or resume. The arrows also aim during
play and navigate menus while paused. Pause or switch away from the window
to release mouse look.

Follow the directions to the three deposits and mine each one. After the
last deposit, **Next: build at cyan pad** gives the direction and distance
back to the build site. Aim at the floating diamond near the cyan posts.
When the prompt appears, press **E** for each of the
foundation, mast and signal head. Aim at the same diamond and press **E**
when the prompt changes to **send rescue signal**.
Use **Save** in the pause menu and wait for the confirmed generation.
**Continue** restores your position, inventory, mined terrain, built
modules and signal state. The default slot is
`%LOCALAPPDATA%\WorldsYetUnseen\WP07`. To test with an independent local
NTFS directory, launch with `--prototype [slot-directory]`.

On an interactive Windows desktop, add `-PrototypeSmoke` to the verified
consumer command. It enables the D3D12 Debug Layer and DRED, drives the
fixed route to the signal and verifies the native window and GPU shutdown.
The smoke submits a frame every second simulation tick to stay within
the provider's bounded diagnostic message buffer.
The route checks blocked actions outside reach and behind the built mast,
the occupied foundation socket, and seam geometry and collision before
mining, after replacement and after unload and reload.

Add `-PrototypeSaveSmoke` for the headless WP07 checkpoint suite. It
terminates Windows writers before and after atomic publication, then uses
fresh processes to verify the mining, construction and completed objective
checkpoints. The verified-consumer CI runs this suite.

### Test world palette

Keep ordinary terrain matte, with brighter top faces and darker side
faces so the shape reads from eye height. Use warm gold for every mineable
deposit and reserve cool cyan for the pad posts. The built beacon uses
distinct module colors so the player can see what was installed.

Use the same palette in every test seed. Pair each accent with a silhouette,
an in-range interaction prompt or both. Deposits protrude above the ground,
the pad has two posts, and the beacon grows upward one module at a time.
Test new colors from the reference spawn and from a close oblique angle.
The scripted route checks that the highlighted seam face disappears when
its deposit is mined.
