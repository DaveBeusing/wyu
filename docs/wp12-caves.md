# WP12-W01 · Ember Caves

From the alpha launcher, open **New World**, then press **Up** to select **New Cave World**. The route uses seed 42 and begins at a collision-checked natural entrance. Walk down the ramp with **W** and use mouse look and the centered reticle. Rock and ore use distinct cues along the route. The HUD identifies the aimed material and shows the ore inventory. Use **E** to mine, **Q** to fill, **F5** to save, or **F10** to save and quit. Reopen the slot through **Continue** in the planet world selector.

The game uses the V2 geology field, protected cave streaming and the atomic terrain/inventory publication from Groundwork. The V2 slot manifest and game payload keep the original WP11 planet save selection available. Ore inventory and terrain depletion come from the same finalized checkpoint. The route is a fixed seed qualification fixture.

## Windows qualification

- Groundwork package `0.1.0-dev.43118949133.1` is pinned in the solution and installer locks. [Immutable archive](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.43118949133.1-immutable)
- On NVIDIA RTX PRO 5000 Blackwell Laptop, driver `32.0.15.9653`, the scripted player descended to 10.3 m below the reference surface. A ray from the player's eye contacted the cave roof before reaching the reference surface. Mining awarded four ore. The fresh process restored generation 1 and terrain revision 2 at the identical underground position, verified the same edit overlay, and committed a simulation tick. Committed member hashes remained unchanged.
- Debug Layer and DRED were enabled. The mining and Continue processes reported eight and two warnings respectively and zero errors, corruption, discarded messages, or live GPU child objects.
- Run `pwsh -File eng/Test-CaveExploration.ps1` on a Windows desktop with NVIDIA and Graphics Tools after the verified consumer build. The harness uses a temporary save directory.

The return ascent and reference production qualification remain open. [Groundwork #89](https://github.com/DaveBeusing/Groundwork/issues/89)
