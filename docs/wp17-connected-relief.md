# WP17 V3 workstation route

Select New V3 World in Planet worlds to open Ember Ridges, seed 42. Its WYUVOL03 manifest dispatches to the connected geology recipe. The existing V1 and Ember Caves V2 selections retain their own manifests and recipes. F6 checks the current published terrain for an exterior launch surface. The first contact from exterior air must be at the feet, face outward, and have clearance for the player capsule. Underground or unresolved coverage blocks takeoff. V3 saves the cave inventory, depletion, terrain edits and player pose. It does not store the separate V1 craft-touchdown receipt.

WYU pins all ten packages to 0.1.0-dev.44118138137.1 from Groundwork master 0d77be0c5da2b9f3ff075a916ead7fa73ac3593f. [Immutable archive](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.44118138137.1-immutable-complete)

Run eng/Test-ConnectedRelief.ps1 after a verified Windows Release build. The two-process route rejects takeoff under the cave roof and qualifies takeoff after the climb. It compares radial heights in the published near collision mesh and the actual orbital render mesh at three matching directions. The near fixture publishes unedited terrain away from the cave route. The tolerance is 0.12 m at each point.

| Feature | Near mesh | Orbit mesh | Absolute difference |
| --- | ---: | ---: | ---: |
| Ridge | +1.9950 m | +2.0001 m | 0.0051 m |
| Main valley | -1.4816 m | -1.5001 m | 0.0185 m |
| Tributary | -0.9901 m | -0.9961 m | 0.0060 m |

On NVIDIA RTX PRO 5000 Blackwell Generation Laptop GPU (driver 32.0.15.9653), the route descended and mined four ore, then reopened revision 2 with matching player pose and overlay digest in a fresh process. It climbed to supported surface, completed the orbital route and returned to the rebuilt near terrain. The edit revision and inventory survived landing at tick 20755. Debug Layer and DRED were enabled, and final shutdown reported zero errors, corruption or live native child objects.

Visual comparison of fixed ground/orbit transects and the reference-class performance gates remain open. WP15 default activation and Groundwork #89 remain open.

The [reference measurement policy](reference-measurement-policy.md) governs
current merge, preview and production decisions. The published-route and
save-compatibility checks still apply to the relevant candidate.
