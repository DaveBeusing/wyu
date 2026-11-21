# Inventory save interruption evidence

The Windows proof for [WYU #75](https://github.com/DaveBeusing/wyu/issues/75) used two independent copies of the same saved Seed 8 world. Each started at checkpoint generation 1, terrain revision 1, inventory revision 4 and environment tick 9000. The four items were owned by a single world bundle. The production Move All operation moved them to the player and the production `WYUGAME1` encoder built a generation-2 candidate with inventory revision 5. Terrain, pose and environment were retained.

## Outcomes

| Fault | State at process termination | Native Continue and verified ownership |
| --- | --- | --- |
| Before checkpoint publication, `AfterIntent` | The intent and private candidate were present. `volume.b` had not been published. The writer was terminated after intent publication and before the checkpoint rename. | Generation **1**, inventory revision **4**. The player carried and equipped nothing. The world bundle owned a chest, backpack, coat and tool, one of each. |
| After checkpoint publication, `BeforeAcknowledge` | `volume.b` already matched the candidate bytes. The writer was terminated before returning the save acknowledgement. | Generation **2**, inventory revision **5**. The player carried a chest, backpack, coat and tool, one of each. Equipped inventory and world owners were empty. |

The native clients were the first readers after each writer was terminated. Both processed the still-present intent, opened the slot with normal Continue, and exited without saving. A separate readback process then independently matched the expected operation nonce, payload bytes, quantities and ownership. Both writer processes terminated with exit code `-1`. The original generation-1 member remained byte-identical in both copies.

## Recorded evidence

| Observation | Before publication | After publication, before acknowledgement |
| --- | --- | --- |
| Writer process | `23376` | `16268` |
| First native Continue | `38528` at `2026-11-21T22:13:32.557Z` | `48728` at `2026-11-21T22:11:09.512Z` |
| Independent readback process | `32096` | `33704` |
| Operation nonce restored | `aacd04db-80f6-49d1-8b2f-537202cb06f7` | `9be37921-53e4-43d5-84fc-1283c0b76064` |
| Interrupted intent SHA-256 | `22AB694A64D6ECC279815881872ACC7F7FA006BC1C6B93E0D39FEDE24FDD34CB` | `77A7261F74B95D6576B46D4B0D1B5F9A5CEDAD8C72F6CB09B0094964DDAF5B4C` |
| Restored checkpoint SHA-256 | `249458C4FA290A691C73F7C850281AF1F5E0A625AB9EA5CFFB9266EF38103C70` | `DCA933D0591E93701EBE10C41ED397B3D59708AECA4D0C3BF80170CF53431A85` |
| Restored game-payload SHA-256 | `9B09F1465840928530123CFEA4B8C1321988E75D0D3A9EF4772A26175AE57D1F` | `9EE11AC77DC0B35F65C0FABF0A600309A5525A4F77E48AF205273A1BCD7DA69B` |

## Provenance and scope

The tested Release client was the local #75 implementation based on WYU `29a42542e2ab5ac7b4d62fde07fd56e2044aea4e`, with client DLL SHA-256 `AB501BDAAF2663CA27F1A7F4672C23CBAB88595278CA0EDF661FCE16B1A04335` and Groundwork producer `067bb610127b7436431a498fc70d0f12c05f8f7b`. The fault harness used the observer available to the pinned Groundwork Persistence test assembly and the real NTFS publisher. This covers the two publication boundaries and native restoration. Candidate capture was prepared by the harness using the production game operation and encoder. CI for the PR is a separate integration check. Historical reference runs were not repeated.
