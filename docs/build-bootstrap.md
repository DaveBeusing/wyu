# Windows host, cube and terrain

Build and run from a clean WYU checkout on Windows with the .NET 10 SDK,
PowerShell 7, and GitHub CLI. The engine is supplied by the immutable
Groundwork NuGet archive. WYU pins all ten packages at
`0.1.0-dev.42303559178.1` and commits the SDK-generated lockfile.
The renderer runs on Windows x64 and requires a D3D12 hardware adapter.

Download the archive to a new temporary directory and run the verification
gate from the WYU repository root.

```powershell
$archive = Join-Path ([IO.Path]::GetTempPath()) ('wyu-archive-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $archive | Out-Null
gh release download archive-groundwork-0.1.0-dev.42303559178.1-immutable `
  --repo DaveBeusing/Groundwork --dir $archive
if ($LASTEXITCODE -ne 0) { throw 'Groundwork archive download failed.' }
./eng/Test-VerifiedGroundworkConsumer.ps1 -ArtifactDirectory $archive `
  -ExpectedManifestSha256 D82D42F2271A5A885E18937B7B0124854268EB3CDDFD1820FCBF84EBE50580E8 `
  -ExpectedSourceCommit 5ab62bbbee7498a178b75a246804709a63238e07 `
  -ExpectedVersion 0.1.0-dev.42303559178.1
```

The gate checks package provenance, stages a temporary external NuGet feed,
restores in locked mode with a fresh cache, builds in Release, and runs
headless startup/shutdown and controlled-fault smoke cases. Add `-WindowSmoke`
on an interactive Windows desktop to open and close a real window under the
same gate. Add `-D3D12Smoke` to verify hardware adapter selection, first
cube presentation and native window close. The D3D12 smoke requests Debug
Layer and DRED. Windows Graphics Tools enables the debug layer and live-object
report. Start the renderer interactively with `--d3d12` and close the window to
stop. Starting without an argument opens the renderer-free host.

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

## WP07 rescue beacon

Launch the Windows client without arguments or use `--prototype`. The
window title displays the start menu, with **New Game** for an empty slot
or **Continue** for a valid checkpoint. Navigate with **Up/Down** and
**Enter**. During play, the title shows the objective, stone, deposit
count and the action in focus. Use **WASD** to move, **Left/Right** to turn,
**Up/Down** to aim, **E** to interact and **Esc** to pause or resume.

Follow the directions to the three deposits and mine each one. Return to
the marked pad, focus its interaction point and build the foundation, mast
and signal head. Focus the pad again and press **E** to send the signal.
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
