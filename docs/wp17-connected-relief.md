# WP17 V3 workstation route

Select New V3 World in Planet worlds to open Ember Ridges, seed 42. Its WYUVOL03 manifest dispatches to the connected geology recipe. The existing V1 and Ember Caves V2 selections retain their own manifests and recipes. F6 starts a flight from a supported surface location, subject to the existing launch and landing checks. V3 saves the cave inventory, depletion, terrain edits and player pose. It does not store the separate V1 craft-touchdown receipt.

WYU pins all ten packages to 0.1.0-dev.43632281999.1 from Groundwork master 905c5682f2988bcd0b74ebeafc01f03ee6b6f01c. [Immutable archive](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.43632281999.1-immutable-complete)

Run eng/Test-ConnectedRelief.ps1 after a verified Windows Release build. The two-process route checks the canonical terrain relief against the orbital mesh sampler at three directions. This is a numeric sample check, with full-route presentation verified separately.

| Feature | Canonical elevation | Orbital sampler |
| --- | ---: | ---: |
| Ridge | +1.9922 m | +1.9999 m |
| Main valley | -1.5000 m | -1.4842 m |
| Tributary | -1.0000 m | -0.9998 m |

On NVIDIA RTX PRO 5000 Blackwell Generation Laptop GPU (driver 32.0.15.9653), the route descended and mined four ore, then reopened revision 2 with matching player pose and overlay digest in a fresh process. It climbed to supported surface, completed the orbital route and returned to the rebuilt near terrain. The edit revision and inventory survived landing at tick 20755. Debug Layer and DRED were enabled, and final shutdown reported zero errors, corruption or live native child objects.

Visual comparison of fixed ground/orbit transects and the reference-class performance gates remain open. WP15 default activation and Groundwork #89 remain open.
