# WP11-W01 reference planet

Far Horizon uses recipe `wyu.reference-planet.v1`, a 4,096 m reference radius
and a 1 m Cartesian lattice. The display name is separate from the recipe ID.
A world supplies its own persistent UUID and an unsigned 64-bit seed. The
planet UUID, generator and terrain schema versions, material catalog version,
and Groundwork recipe parameter digest are recorded in the descriptor.

The Groundwork v1 landform blends continent elevations between −24 and 32 m
at a 1,024-cell scale. Mountain ridges, valleys and detail use scales of
192, 96 and 24 cells respectively. The material field assigns basalt (1) and
ochre stone (2) in 48-cell regions. Rendering and gameplay resource rules
for these materials belong to subsequent WYU work.

## Fixture audit

The test executable records both hashes for each seed and checks them against
the committed fixtures. Each hash covers a 9 × 9 × 9 lattice region in X-fastest
order. Each record is a little-endian signed 64-bit density and unsigned
16-bit material. Macro samples are spaced 256 cells apart around
`(0, 4096, 0)`. Local samples are spaced one cell apart around the rounded
first spawn candidate. All three seeds have different hashes at both scales.

| Seed | Collision-confirmed spawn X, Y, Z (m, Y rounded) |
| --- | --- |
| `42` | `16, 4104.499, 16` |
| `20261111` | `48, 4134.532, 16` |
| `18446744073709551615` | `16, 4089.335, 16` |

After setting up the verified feed, reproduce with
`dotnet run --project tests/WorldsYetUnseen.Gameplay.Tests -c Release`.
The tests recreate the manifest, compare serial and parallel sampling, and
check shared density and material at every face edge, including the corners.
They stage a real terrain mesh and publish it through Groundwork Core before
testing the spawn and landing footprint. The traversal probe sweeps a capsule
2 m along +X from the spawn, then back. For the future camera preview, start
over the verified footprint at +12 m in Y and look down.

## Host integration

Try the eight candidates in order. The first step estimates a surface from
canonical density on the +Y face. Construct the coordinator with this recipe
instance's descriptor and stage its density field as the source. The second
step acquires the coordinator's currently published collision. A candidate
passes only when the nine
landing rays have one walkable hit each, the footprint elevation agrees with
its center to within 0.5 m, and the capsule has a clear vertical approach.
Seed `20261111` exercises fallback to the second candidate. If coverage is
missing, publish coverage for the candidate before retrying it. If every
covered candidate fails, report that the seed has no verified spawn. Revalidate
the returned terrain identity at spawn publication. W02 wires this policy
to the generated-world host.
