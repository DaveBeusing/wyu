# Worlds Yet Unseen — World Vision, Lore & Gameplay Canon

**Stand:** 13. November 2026  
**Status:** Konsolidierte Designentscheidungen, keine Aussage über Implementierungsstand.  
**Geltung:** Eine zentrale Referenz für Spielvision, Lore und beschlossene Gameplay-Regeln; Groundwork bleibt eine eigenständige Engine.

## 1. Core promise
*Worlds Yet Unseen* is a first-person, single-player survival and discovery game on procedural, persistent, deformable voxel worlds. Its emotional pillars are solitude, wonder, freedom, gradual unease and discovery. The player is free to settle, build, explore and eventually travel into space; escaping the starting planet is not a mandatory ending.

**Opening thought:** “Ich sitze hier fest, muss überleben und herausfinden, ob ich bleiben muss oder weg kann.”

**Central mysteries:** “Wer außer mir hat überlebt?” and “Warum ist dieser scheinbar perfekte Planet nicht sicher?”

Progression is driven by practical capability and discoveries, not a forced sequence of story missions or prescribed vehicle classes. WYU owns game-specific rules and lore; Groundwork remains a reusable, lore-neutral engine.

## 2. Human mission and opening
Humanity is undertaking an early, voluntary phase of interstellar colonization, not fleeing an uninhabitable Earth. A large colony ship carries thousands of cryosleeping passengers toward a probe-identified habitable world roughly 10–15 light-years away. Travel is sublight, without FTL. The planned settlement would deploy people and equipment by landing craft while the mother ship remained in orbit.

The player is a civilian colonist with basic technical training. During approach, a severe stellar event disrupts the mission before normal colonization begins; some cryogenic units function as emergency landing capsules. The player's damaged capsule reaches the planet. Its emergency systems keep the occupant asleep for months until dwindling energy forces awakening. The game opens **inside the damaged capsule**, without a playable shipboard prologue or contrived amnesia. The capsule provides limited shelter, tools, fragmentary data and a local beacon, but not a full explanation of the disaster. The player's personal identity and specialization are not prescribed beyond basic technical training.

The mother ship's planned role was to remain in orbit while landing craft transferred colonists and equipment. Its final fate and location are **not canonically settled**; a wreck, dispersed debris or surviving orbital section remain possibilities. Other capsules, wrecks and small outposts may exist; some other humans may have awakened months earlier. The player should feel alone for a substantial early period; do not provide an early functioning human settlement or a large group of NPCs. Human traces precede any actual survivor encounters.

## 3. Planet, atmosphere and exploration
The initial world appears largely Earth-like: forests, plains, mountains, water, deserts, wetlands and cold regions transition plausibly. Visual style is high-quality, lightly stylized realism, with selective alien plants, creatures, geology and bioluminescence rather than constant fantasy spectacle. A continuous day-night cycle and regionally varying wind, rain, fog, snow, thunderstorms and ordinary storms shape exploration. Weather may affect visibility and access, without requiring a fully physical climate model. Biomes should transition plausibly rather than obey an arbitrary numerical Earth-like/alien distribution.

The planetary surface is fully modifiable: mining, tunnels, caves, underground bases and permanent terrain changes are core freedoms. Resources occur in finite geological deposits, including underground deposits. Basic resources must be accessible near the start. The world map is revealed by exploration/scanning rather than fully exposed; the initial scanner reaches shallow deposits, with upgrades expanding capability. Landmarks and human traces are discovered organically, not guaranteed scripted encounters.

## 4. Narrative pacing
**Early:** peaceful isolation, basic survival, first shelter and tools, a beautiful world that feels inviting. Alien clues are subtle and ambiguous; no early direct meeting with an intelligent alien or established human community.

**Middle:** growing infrastructure, exploration, abandoned human traces, mysterious artifacts and environmental anomalies. Some signals may be detected but not deciphered. Danger and uncertainty rise without removing freedom to remain and build.

**Late:** clearer proof of intelligence, possible encounters with humans and aliens, larger discoveries and more demanding environmental events. These are tonal phases, not hard level gates or scripted hour counts.

## 5. Stellar activity and storms
The star has a recurring activity pattern: a solar storm every **30 in-game days** and an extreme superstorm every third occurrence, i.e. every **90 days**. The initial sequence is weak at day 30, medium at day 60 and extreme at day 90. Each event spans a storm day and a decay day; aurora on the two preceding nights provides warning. The opening remains relatively forgiving: the first weak storm poses a radiation concern but no electronics disruption; medium storms bring temporary radio/machine problems; extreme storms temporarily disable exposed active systems without destroying batteries or draining their stored charge.

Rock cover and underground/in-mountain bases provide meaningful protection for people and equipment. This protective advantage need not be explicitly disclosed at the beginning. Radiation protection for constructed shelters depends on suitable materials; ordinary wood is not adequate shielding. Stronger protective technology becomes available later. The storm cycle provides longer-term pressure and mystery, but **does not force evacuation or spaceflight**.

**Lore distinction:** The stellar event during the colony ship's approach is established in planning, but the detailed causal chain, ship damage and final outcome remain to be written. Alien civilization does not cause or control the storms.

## 6. Native life and alien civilization
The world supports relatively few distinctive, biome-associated animal species. Most are harmless; some can threaten the player. Wildlife interactions, including occasional predator/prey behavior, should feel emergent rather than staged. Plants provide food, materials and rare medicinal uses, with scanning needed to identify medicinal properties.

An **active, native, technologically advanced intelligent civilization** exists on the starting planet, living predominantly underground as protection from stellar conditions. Its anatomy is visibly nonhuman. Its architecture should feel organic and alien, with discernible technological details; its technical sophistication exceeds humanity's in ways not yet fully specified.

Early evidence comprises ambiguous anomalies, abandoned structures, artifacts and undecipherable radio signals. The player should not know with certainty until relatively late that a living intelligence is present. Alien materials are not directly exploitable at the start; research is needed. The civilization has noticed the colony ship but initially does not know where the scattered capsules and survivors landed.

**Open:** alien language, society, motivations, first contact, relationship to older ruins and precise technology. Do not presume universal hostility or friendship.

## 7. World construction and technology foundations

- Modular construction supports freely designed bases, vehicles and spacecraft, with optional snap alignment and flexible placement. Built structures have no collapse simulation; voxel terrain remains deformable and persistent.
- Resource deposits are geological and finite, potentially deep underground. Research is gated by discoveries, equipment, materials and time rather than abstract research points.
- The broad progression is survival/tools, base/power, machinery/automation, vehicles/advanced energy and space exploration. This is a capability progression, not a mandatory fixed build order.
- Survival combines hunger, health, ambient temperature, radiation and a small number of legible injuries. Sleeping is optional; there is no separate thirst meter.
- A home base can remain viable throughout the game; underground construction and appropriate materials protect against later solar events.

---

# Detailed gameplay rules

## World and exploration
- Planetary-scale continents, landforms and biomes must remain recognizable during orbital approach; scalable LOD/chunk streaming is an engine concern.
- Resource deposits are finite; starting areas must contain reachable basic resources.
- The map is discovered through exploration/scanning, not fully revealed at start. Landmarks are procedurally placed, without guaranteed fixed nearby wrecks.
- The rescue capsule emits a limited-range home beacon. Players may create local waypoints and optionally transmit them over radio.
- The starting multitool scanner identifies materials and detects shallow subsurface resources to approximately **5 m**; upgrades extend depth.

## Inventory, storage and logistics
- Player inventory and storage are **weight-limited only**, not volume-limited. Backpacks increase carry capacity; identical items stack.
- Early game offers one simple, unpowered storage chest; larger containers for advanced bases/ships share the same storage abstraction. Exact capacities are TBD.
- Dismantling a chest drops all contents on the ground. Provide an **Move All** transfer action.
- Advanced machines can use shared storage through connected logistics networks; early workbenches are manually supplied.
- Logistics are fully opaque, enclosed **rectangular conduits for solids** and **round pipes for liquids/gases**; no electricity consumption, no rendered per-item motion and **no throughput cap**.
- Connected machines request inputs automatically. Splitters branch networks; filters only allow/block material types, never allocate percentages.
- Supply arbitration is FIFO by request time; an unsatisfiable request may be skipped without losing its original priority. Reserve/withdraw all required recipe inputs atomically when the complete set is available.
- Machines poll for material demand at an initial **1-second** interval (implementation may be event-driven with equivalent semantics).
- Finished products transfer automatically to a connected container when possible; otherwise to an internal output buffer of **8 stacks**, one item type per stack. When no output space remains, production pauses.
- Power loss pauses machines and preserves materials and exact progress; work resumes automatically on restored power. Dismantling an active machine cancels production and drops input materials and completed output on the ground; elapsed progress is lost.
- Power shortage initially shuts down affected equipment without automatic priorities; later physical logic components can implement priorities.
- Initial production-time balancing: simple 5–10 s, standard 10–15 s, complex 20–30 s, advanced 30–60 s. Advanced machines operate at roughly **2× speed** and can run **2 parallel jobs**.

## Multitool and research
- The multitool starts with basic modules. Interchangeable upgrades can improve efficiency and unlock functions, swapped instantly at a workbench.
- Initial upgrades: enhanced mining module, energy-efficiency module, precision-building module. No use-based leveling requirement was confirmed.
- Research combines exploration/scanning and a discovery-gated research tree. Discovery gives immediate information; development at research equipment takes time and resources. No abstract research-point currency.
- Research continues while the player is away, provided power and materials remain available. Discovered blueprints remain unlocked.
- Initial research takes place using the damaged rescue capsule; a separate research station comes later. Initial technology candidates: basic workbench, enhanced mining module, simple generator.
- Capsule guarantees **metal parts, basic electronics and a small energy store**, plus **one randomly selected extra** from copper wire, seal kit, or emergency battery.

## Construction, vehicles and controls
- Freely placeable blocks, **no structural stability or collapse simulation** for constructed buildings; floating structures are allowed. Optional snap/alignment, transparent preview and clear connection/error feedback. Blueprints unlock later.
- Vehicles of any type are freely assembled from blocks, inspired by Space Engineers; no prescribed vehicle class or fixed type progression. Material and research availability gate construction.
- Moving mechanical components (wheels, hinges, joints, pistons, rotors) are part of the planned building system, unlocked through research.
- Vehicles share the base power-network model. Driver seats/cockpits and remote control are both allowed; controls may be mapped to components and eventually automated.
- Damage is per block; components have functional consequences and may be repaired in place with the multitool and required materials.
- Vehicle physics should be simplified but perceptible: mass, center of mass and inertia influence handling, without simulating every block independently.

## Wireless logic and energy
- Physical logic components include switches, timers, power sensors and temperature sensors; modules communicate wirelessly.
- Each wireless hop has a **250 m** range and works through terrain/buildings (no line-of-sight checks).
- Range extenders may be chained without an artificial total distance or extender-count cap; use event-based signal propagation, caching and cycle protection.
- No high-end storm-immune radio modules are planned.

## Weather, thermal protection and wind
- Ambient temperature can harm health. Clothing provides thermal protection; shelters offer stronger protection and the effects combine. Wind has no direct player-health effect.
- Initial ambient-temperature guidance: comfortable 15–25 °C; cold stress increases below about 5 °C, serious risk below 0 °C; heat stress above about 35 °C, serious risk above 40 °C. Effects depend on exposure time and protection, not instantaneous threshold damage.
- Enclosed buildings have their own internal temperature, influenced by heat/cooling sources.
- Wind influences **wind generators only**. Wind varies by biome, terrain exposure and elevation (peaks stronger than valleys), with smoothly changing random targets every **2–5 in-game minutes**.
- Wind generator output is between **25% and 100%** of rated output. Tower height bonus to regional baseline: ground **0%**, raised **+10%**, high **+20%**; clamp final output to rated maximum. Generator output can be updated about once per second; share regional wind computations.

## Solar storms and radiation
- Solar storms occur every **30 in-game days**; every third is an extreme superstorm (**90-day cadence**). Initial progression: day 30 weak, day 60 medium, day 90 extreme. Each event spans two days (storm and decay).
- Aurora warning appears on the **two preceding nights**, with intensity reflecting the incoming storm. Later local instruments can forecast storm strength more precisely.
- No last-minute mandatory protective switch. The player prepares through shelter, construction and resources.
- Radiation protection depends on **building material, not wall thickness**: wood offers weather protection but negligible radiation shielding; advanced metal structures attenuate radiation; specialized late-game materials can fully protect. Closed substantial rock cover/underground bases protect people and technology.
- Weak storm: radiation hazard but no electronics disruption. Medium: temporary machine/radio disruptions. Extreme: exposed active machines/networks temporarily stop; batteries retain charge and machines are not destroyed.
- Radio is stable in weak storms; medium/extreme storms disrupt it, especially long-distance connections. Radio disruption does not itself destabilize electrical grids. Locally powered machinery can run independently of remote control, subject to direct storm effects.
- Natural aurora remains visible independently of radio; local forecast instruments do not depend on radio but require functional power/equipment.

## Survival and medical care
- Sleep is optional, useful for recovery/night skipping rather than a mandatory need. No separate thirst meter. Hunger rises slowly.
- Health, hunger, temperature, radiation and injuries are relevant; keep feedback explicit and understandable.
- Use a small number of distinct injury types with clear effects (e.g. fractures affect movement, burns, radiation injuries).
- Minor injuries and health recover slowly naturally; first-aid supplies speed treatment; severe injuries require appropriate medical treatment, with advanced medical stations later.

## UX principle
- Do not make players guess why crafting, building, machines or other actions fail: show explicit actionable reasons such as missing material, missing power, invalid connection. Mystery belongs to world exploration, not opaque interface failures.

## Deferred and not yet specified
- Exact item stack sizes and container weights; output buffer when multiple output destinations are full.
- Blueprint workflow, moving/rotating/copying large structures, exact module costs.
- Research durations, recipe costs, unlock prerequisites, scan upgrade tiers.
- Wind zone granularity, radio propagation edge cases, detailed thermal/radiation formulas.
- Starting biome distribution, specific vehicle designs and technology order.
- Implementation architecture and acceptance tests belong in separately scoped work packages.

## Additional agreed gameplay decisions

### Wildlife, botany and alien traces
- Fauna consists of relatively few distinctive species rather than densely populated wildlife. Most are harmless; some are dangerous.
- Species are biome-dependent; encounters emerge through exploration rather than scripted story events. Threats react to proximity and circumstances and should allow fair chances to react, flee, hide or deter.
- Occasional group and predator/prey interactions are ambient, not constant spectacles. Early deterrents are limited; research broadens options.
- Plants provide food, materials and rare medicinal uses; medicinal properties require reliable identification by scanning.
- Early alien clues remain subtle and ambiguous, including environmental anomalies, artifacts, structures and undecipherable radio signals. Definitive evidence comes relatively late.
- Alien architecture is organically unfamiliar with technical details. Alien materials require research before exploitation. Alien society and later interactions remain open.

### Production philosophy
- WYU is not a factory optimization game. Automation primarily removes repetitive work and permits freely arranged production facilities.
- Solid items use opaque square/rectangular enclosed conduits; liquids and gases use round pipes. No individual objects are visibly simulated in conduits.
- All logistics conduits have unlimited throughput. No belt-speed, flow-rate or allocation-percentage optimization puzzles.

### Death and respawn
- Death is player-friendly. Respawn at the rescue capsule or a later designated respawn point.
- Constructions, research and stored resources persist. Carried equipment remains at the death location for recovery and never expires on a timer.
- Respawn is a gameplay convention and needs no cloning or resurrection explanation.

### Modular spacecraft and flight
- Ships, probes and other vehicles are freely constructed from modular blocks; no prescribed first spacecraft.
- Planetary surface, atmosphere and space connect seamlessly without loading screens.
- Simplified Newtonian physics includes inertia, acceleration and counter-thrust, with optional switchable flight assistance. Aggregate mass and center of mass without per-block rigid-body simulation. Gravity is simplified; exact orbital mechanics are not required.
- Docking offers magnetic-feeling alignment/snap assistance. Launch and landing fuel requirements are forgiving; re-entry heat is abstracted without detailed heat-shield micromanagement.
- Only player-designated pressurized compartments require airtight enclosures and functioning life support. Open external structures are permitted.
- Groundwork checks enclosure integrity when relevant blocks or doors change; pressure is per compartment, not per gas particle. Leak-size-dependent gradual depressurization allows response to small leaks while large breaches depressurize quickly.
- Longer journeys use a researched, faster **conventional sublight** propulsion system, never warp, hyperspace, FTL, teleportation or scene cuts.
- The player manually controls acceleration and braking, with no forced cutoff near celestial bodies. Propulsion consumes fuel/energy when thrusting or braking, not while coasting.
- Asteroids can be mined, tunneled through or completely destroyed. Space navigation includes a map and local player-set markers; future sharing should remain architecturally possible.

## Canon and conflict resolution
- **Building statics:** No collapse simulation for constructed buildings; floating structures are allowed. This supersedes the early exploratory suggestion of structural stability. Deformable voxel terrain is a separate system.
- **Stellar storms:** Every 30 in-game days, every third extreme; the early game remains forgiving because severity escalates. This supersedes any suggestion that all storms begin only late.
- **Logistics:** Unlimited throughput and comfort-focused automation override proposals for constrained capacity.
- **Spaceflight:** Conventional sublight propulsion, seamless traversal and simplified gravity. No fixed vehicle progression.
- **Pressure:** Only selected compartments must be sealed; leaks cause progressive, size-dependent pressure loss.
- Balancing numbers are initial design values unless explicitly fixed. Do not treat unresolved story questions as established canon.

## Open decisions and next planning area
- Fate of the colony ship, detailed disaster sequence, alien language/society and first contact, specific survivors and encounter timing, named celestial bodies, detailed story events and optional endings.
- Base administration and energy management are the next design discussion.
- Detailed balancing, exact interfaces, algorithms and acceptance tests belong to future scoped work packages.
