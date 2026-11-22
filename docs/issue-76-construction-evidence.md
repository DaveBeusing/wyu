# Static construction evidence for WYU #76

The first slice has two blocks on a 25 cm grid. A cube debits one ore and a two-cell block debits two. Dismantling refunds the full amount through the inventory capacity and overload rules from WYU #75. The equipped building tool is required in both directions. A published world root owns the block layout and the resulting inventory together. Removing a supporting block leaves other blocks in place.

Each grid keeps its planet-space radial frame through rebases and saves. The new versioned section of `WYUGAME1` holds up to 32 anchors and 1,440 blocks in 24,080 bytes of the 24 KiB construction allowance. Existing payloads open with an empty build state. Unsupported versions follow the protected save-slot handling. Block 1,441 is rejected before publication.

## Published package pin

The package pin reached WYU `master` at `e923f5a9b8fe24765c96451f9c4192107692c02c` with [WYU PR #88](https://github.com/DaveBeusing/wyu/pull/88). The construction branch targets `31371e40fb11c3a1f4b83a0588bec112c24d9823` after [WYU PR #89](https://github.com/DaveBeusing/wyu/pull/89). Its locked restore uses Groundwork `0.1.0-dev.45005277801.1` from `751a1e022d703c6dfd582c6521cd1cc4f9c6a2eb`. The published packages provide the static obstacle sweep and D3D12 construction-scene receipts used by the client. [Immutable tag `archive-groundwork-0.1.0-dev.45005277801.1-immutable-complete`](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.45005277801.1-immutable-complete)

The [WYU master consumer CI](https://github.com/DaveBeusing/wyu/actions/runs/45011468250) passed for `e923f5a`, including release provenance, locked build, host lifecycle and save recovery. See the [verified consumer build](build-bootstrap.md) for the locked restore and build procedure.

### Verification against the published packages

The local locked Release build on `e923f5a` with the construction patch passed, and the SHA-256 of each of its ten Groundwork DLLs matched the DLL in the corresponding released NuGet package. The build used typed Groundwork calls for static obstacle sweeps, transparent props and scene Present receipts. Client SHA-256 was `E3DC05220BA3CA1516F34C58A7BA40FAEDEE5FACEF249A6D420CF8C42A122901`. All three construction gameplay checks, six collision checks and both checkpoint checks passed.

The first focused WARP run caught an ordering window between the scene receipt and publication of the renderer frame status. The movement hold now waits for both. A fresh fixture restored generation 2, verified the floor and wall, and confirmed the same building across the rebase at Present 6. After the test dropped sequence 7, Present 8 confirmed the same committed removal, with two ore refunded and one wall remaining. The player fell `2.725 mm` on the next tick. The two durable checkpoint hashes were unchanged. The measured latencies, color and depth samples, and save fault results below come from the earlier qualification.

## Earlier local overlay qualification

The D3D12 readback, native motion, checkpoint interruption and measurement evidence below was collected on a local overlay based on WYU `5b20d2781649a685cd3604c8d3e0ed486d83c71f` and Groundwork `067bb610127b7436431a498fc70d0f12c05f8f7b`, the producer of `0.1.0-dev.44510519303.1`. The overlay used three locally built assemblies with the static obstacle and construction-scene receipt patch. The patch SHA-256 was `128ADA11AF795616D48E19DAB9E740D6B0FFADDCBF4546D2C4CF15A7020DDA4C`. The reproduced Release candidate reported client SHA-256 `916BCFBE5654CF0764CB795417214F0E85356977DB9B87EB8798B00C319986C7` and Groundwork source archive SHA-256 `27F60A597DF0573BD5B673FDA8468D685BAF32F3AD15CCF207663D2D0BB5CD30`.

### Targeted verification

- Gameplay checks cover grid boundaries and rotations, cross-anchor overlaps, tool and ore checks, capacity and refund, a repeated dismantle, and the 1,441st block limit. The Checkpoint test covers the exact maximum mixed build state, the shared save budget, old payloads, malformed records, and unsupported versions.
- WARP color/depth readback covered committed geometry, a preview outside the camera, the visible transparent preview and removal. The transparent preview changed the sampled color from `FF0000FF` to `9E0061FF` while preserving depth `0.975976`. The preview outside the camera received no visible-preview receipt. Removing the block restored the original terrain color and depth.
- A native motion fixture showed that the built floor supported the player and that the wall blocked movement before penetration. Present 5 confirmed the rebase with the same collision result, pose, block IDs and inventory. After the floor and its ore refund committed, the proof withheld camera sequence 7 before handing it to the owner. Movement stayed held until Present 8 confirmed that same committed root. The player fell `2.725 mm` on the next tick while the unsupported wall remained. This test controls a lost submission, with an ordinary successful native Present for the retry.
- The reproduced Release candidate opened an independent copy of the motion fixture in a native Continue. PID `33276` restored generation 2 with two blocks, one anchor and five carried ore, confirmed construction Present 3 and exited with code zero.

The Release gameplay test runner has a `--construction-only` filter. The client exposes `--inventory-checkpoint-proof` and `--construction-collision-proof` for headless checks. Its native `--construction-motion-proof`, `--construction-continue-proof` and `--construction-measure-proof` routes require separate marked fixture directories. These routes are independent of the historical reference runner.

## Checkpoint interruptions

The writer used the real NTFS checkpoint publisher and the production operation and encoder with four independent fixture copies. Each fresh native Continue read its interrupted slot first and exited with code zero after Present sequence 3. An independent readback also checked the operation nonce, canonical block records, carried ore, and SHA-256 of both durable members.

| Operation and interruption | Native PID | Restored generation | Blocks | Carried ore | Restored checkpoint SHA-256 |
| --- | ---: | ---: | ---: | ---: | --- |
| Build before publication | 2860 | 2 | 0 | 6 | `F6055ACC02D752352BC708C3625F2BA863609A5EB60FD5C398BA0804F91F993A` |
| Build after publication, before acknowledgement | 35536 | 3 | 1 | 4 | `B1EA4715A61753D62CD4820A081A0D41BDE2CA1C2B28B7C6FCAE8C8241058CCD` |
| Dismantle before publication | 39168 | 2 | 1 | 4 | `5DFADF657A859A450EEA71B0F8AAD20F6F9876E58C6C798A5A9592FC4A50598A` |
| Dismantle after publication, before acknowledgement | 7316 | 3 | 0 | 6 | `AAA0525B8A27B51D9DE0ABC96840A3F97C3168FAD6635C195E1037F5F005D2D5` |

The interruption client SHA-256 was `CE5C2D3985AB0D77749C04CE43DAF5E4208BA155E43CFA8B5BE38915D97AD7BF`. Subsequent overlap, movement and frame retry fixes did not touch the checkpoint decoder, publisher, or core build and dismantle transactions. The original four reference runs were not repeated.

## Workstation measurements

The native WARP fixture uses 20 warm-ups and 100 samples at each size. Preview planning times validation of a fixed candidate against the current published world. Place and dismantle timings end at the root commit. The two Present intervals start there and stop at the matching native receipt. The preview allocation counter covers the calling managed thread. Commit allocation covers the managed process and includes scheduling or background work during the interval. The sampled memory value is the largest observed process working-set increase over that same empty fixture. These measurements are specific to the WARP workload. The contract introduces no performance ceiling.

The retry and measurement client SHA-256 was `4FB1914E6554BCFE2DAB477EA4A3E1C134E9AD165714B90B6D9A7DD320F027AF`. The later review adjustments affect interactive read-only, reticle and targeting feedback. The diagnostics use a fixed fixture candidate and do not use these UI paths.

| Blocks | Preview plan p50/p95 (ms) | Place commit p50/p95 (ms) | Dismantle commit p50/p95 (ms) | Place Present p50/p95 (ms) | Dismantle Present p50/p95 (ms) | Mean allocation preview/place/dismantle (B) | Sampled working-set increase (B) |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 16 | 2.654 / 8.415 | 3.474 / 7.494 | 0.041 / 0.085 | 246.394 / 256.381 | 240.897 / 251.349 | 840 / 9,083 / 8,973 | 3,854,336 |
| 256 | 2.193 / 6.922 | 3.119 / 5.335 | 0.053 / 0.115 | 246.831 / 255.502 | 242.836 / 252.599 | 4,685 / 83,349 / 83,718 | 16,588,800 |
| 1,440 | 1.897 / 3.813 | 2.824 / 4.368 | 0.193 / 0.344 | 246.976 / 255.313 | 240.578 / 252.465 | 23,641 / 454,112 / 452,111 | 51,601,408 |

At the limit, all 100 attempts to place block 1,441 were rejected by the domain check. That check took `2.40 / 3.80 µs` at p50/p95 and allocated zero managed bytes on the calling thread.

## Review and delivery

Independent code and security review of the earlier overlay candidate followed the corrected paths for object clearance, collision during foot movement and flight return, atomic refund, save validation, and Present retry. The UI and UX source review checked controls, the crosshair, read-only handling and action feedback. Rendered evidence covers the native preview. Manual keyboard interaction and CI for the construction branch remain separate checks.

WYU `master` is the default and integration branch. The [earlier baseline consumer CI](https://github.com/DaveBeusing/wyu/actions/runs/44960591223) passed for `5b20d2781649a685cd3604c8d3e0ed486d83c71f`. At overlay qualification, WYU PR #83 overlapped client rendering and host files, and Groundwork PRs #186 and #193 touched the renderer files covered by the overlay. Groundwork `master` was `8317a841f6b24bfd1c2ca84d1893a750add9e4b2` at that verification.
