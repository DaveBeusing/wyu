# WYU — Gameplay design follow-up (10 November 2026)

**Status:** Agreed gameplay design requirements, not implementation. Supplements `docs/Gameplay_Decisions_2026-11-10.md`; where the discussion explicitly clarifies earlier assumptions, the clarification takes precedence. Exact numerical balancing remains TBD.

## Flora, fauna and alien presence
- Fauna consists of relatively few distinctive species rather than densely populated wildlife. Most species are harmless; some dangerous species exist.
- Species are biome-dependent, and the player can learn recognizable biome associations. Encounters are emergent open-world events, not scripted story encounters; dangerous encounters may eventually occur through normal exploration.
- Dangerous creatures react to proximity and circumstances. Encounters may surprise the player, but should allow a fair opportunity to react, flee, hide or deter the threat.
- Wildlife can interact with other wildlife, including groups and visible predator/prey behavior. Such interactions should be uncommon and mostly ambient, not constant spectacles.
- Early deterrents are available but limited; research can broaden deterrent options.
- Plants can provide food, materials and medicinal uses, not merely decoration. Medicinal plants are rare and their properties must be reliably identified by scanning.
- Early indications of the active alien civilization are subtle and ambiguous: environmental effects, artifacts/structures and undecipherable radio signals. Clear proof of intelligence comes relatively late.
- Alien architecture is organically alien with recognizable technical details. Alien materials are not initially directly exploitable; research is needed before use. The exact alien society and later interactions remain open.

## Production and logistics clarification
- WYU is **not a factory optimization game**. Automation primarily reduces repetitive manual work.
- Solid items travel through opaque, square/rectangular enclosed conduits; liquids and gases use round pipes. No per-item visible motion.
- Logistics conduits have **unlimited throughput**, as already specified in the main decisions document. Do not introduce belt-speed or pipe-throughput optimization puzzles.
- Production facilities can be freely arranged; automation is a convenience and progression system, not the central gameplay loop.

## Death and respawn
- Death is deliberately player-friendly. Respawn occurs at the rescue capsule or a subsequently designated respawn point.
- Player constructions, unlocked research and stored resources remain intact.
- Carried equipment remains at the death location and can be recovered; it does not despawn on a timer.
- Respawn is an accepted gameplay convention; no cloning or resurrection lore explanation is required.

## Spaceflight and ship construction
- All spacecraft, probes and other vehicles are freely constructed from modular blocks; no prescribed first spacecraft or probe, and no forced vehicle progression.
- Planet surface, atmosphere and space are traversed seamlessly without loading screens.
- Flight uses simplified Newtonian behavior: inertia, acceleration and counter-thrust, with optional switchable flight assistance. Mass and center of mass matter without per-block rigid-body simulation. Exact orbital mechanics are **not** required; gravity is simplified.
- Docking offers a magnetic-feeling alignment/snap assist, avoiding exacting manual alignment.
- Launch and landing fuel requirements should be forgiving; re-entry heating is abstracted without detailed heat-shield micromanagement.
- Ships need not be airtight overall. Only player-designated pressurized compartments require an airtight enclosure and functional life support. Open external construction is allowed.
- Groundwork should detect enclosure integrity when relevant blocks/doors change. Pressure is modeled per compartment rather than per gas particle. Leaks cause **gradual pressure loss depending on leak size and conditions**: small leaks allow time to respond; major breaches depressurize quickly. Do not treat every leak as instant vacuum.
- Long-distance travel may use a researched **faster conventional propulsion system**, not warp, hyperspace, teleportation, faster-than-light travel or scene cuts.
- Travel acceleration is manually controlled; no forced automatic drive cutoff near celestial bodies. Propulsion consumes fuel/energy when applying thrust, including braking; coasting at constant velocity in space does not require ongoing propulsion consumption.
- Asteroids may be mined, tunneled through and completely destroyed; resource distribution remains a balancing concern.
- Space navigation includes a map and player-set markers. Markers remain local in the initial single-player scope; future sharing should not be architecturally blocked.

## Remaining scope
- Base administration and energy management are the next proposed design discussion.
- Detailed balancing, simulation algorithms, technology costs, exact UI and acceptance criteria require subsequent work packages.
