# WYU — Gameplay design decisions (10 November 2026)

**Status:** Agreed design decisions; not implemented. This document supersedes conflicting older conceptual statements in `docs/World_Vision.md` for the topics explicitly covered here. Numbers described as initial balancing values remain tunable.

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

## Compatibility note
Earlier world-vision text suggests structural stability for buildings and leaves storm timing open; the explicit decisions above take precedence for those topics. Keep planet-scale voxel terrain deformation distinct from building stability. These are design requirements, not claims that the current game or Groundwork already implements them.
