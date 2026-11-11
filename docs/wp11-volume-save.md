# WP11-W03 terrain edits and checkpoints

From the alpha sidebar, open **New World** to enter the planet world
selector. Select **New World** again to reserve a separate world with a seed,
or select **Continue** to open a committed world. The planet slots live in
`%LOCALAPPDATA%/WorldsYetUnseen/WP11/v2`.

- Aim at terrain 3.5–10 m away. Press **E** to remove or **Q** to add a small
  3D brush. Press **1** for basalt or **2** for ochre stone.
- **F5** saves. **F10** saves and closes the window after confirmation.
  **Esc** closes the window without saving new changes.
- An incompatible slot stays protected. **New World** allocates a distinct
  slot. A recovered checkpoint can be explored read-only. The alpha mission
  retains its own save location.

The host seals each brush at BeginTick and holds movement while the selected
window is rebuilt. Groundwork publishes the edited overlay, mesh and
collision in one Core root. WYU reports success after D3D12 presents that
generation. A checkpoint takes the finalized cut, then the I/O worker
writes it through the two-member NTFS protocol. The restore path rebuilds
terrain from the saved seed and overlay before admitting movement at the
saved position.

## Windows verification

After a Release build with the [verified feed](build-bootstrap.md), run
`./eng/Test-VolumeSave.ps1` on an interactive Windows desktop with Graphics
Tools installed. It uses a disposable fixture under the Windows temp
directory and launches a fresh WARP process for each phase.

The fixture rejects an out-of-coverage brush, removes terrain at a brick
border, adds an ochre ledge, and shifts the active window away and back.
It checks the density, material, collision and exact overlay and pose after
normal restart. For the interruption, it holds the destination member open
for reading, lets the writer install the new intent and hit the blocked
NTFS rename, then terminates that writer. Both committed files must remain
byte-identical and Continue must rebuild generation 2 in a fresh process.
An incompatible occupied slot must also retain its original bytes.
