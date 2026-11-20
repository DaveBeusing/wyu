# Package G · Far Horizon sky

Far Horizon uses the profile `wyu.far-horizon.atmosphere.v2`. The planet radius
is 4096 m and the shell is 112 m thick. Rayleigh and aerosol scale heights are
40 and 14 m. The Rayleigh scattering coefficients per metre are
(0.0012, 0.0026, 0.005) and the extinction coefficients are
(0.0014, 0.003, 0.006). Aerosol scattering and extinction use 0.002 and
0.0024 in each channel, multiplied together by `1 + 0.35 * haze`, where haze
is the existing local weather opacity clamped to [0, 1]. Its existing altitude
fade remains active. Solar radiance is 16 and the disk radius is 0.009 rad.
The star seed is the XOR of the high and low 32-bit halves of the world seed.

The sky reads Package E's accepted clock and presentation fraction. The walking
camera, held terrain mask and paired terrain frame use canonical eye and view
data. The near and orbital flight renderers share the same flight camera and
projection angle. Local terrain fog continues to use Package E. Continue
reconstructs the sky from seed, pose and tick in the existing checkpoint.
Ember Caves retains its own presentation. Set `WYU_PACKAGE_G=off` to use the
clear-color fallback and the Package E near and orbital cameras while retaining
the Package E weather rules.

Run `eng/Test-ReferenceSky.ps1` with the pinned Release build. Repeat with
`-Adapter warp`. The fixture starts seeds 42 and 20261111 at tick 35500, checks
twilight, the first far presentation during ascent, orbit outside the
atmosphere, the first near presentation on return, and the landed sky after
Continue. On an unobstructed Windows desktop the test freezes the accepted eye
and tick at both ownership transitions and captures three paired views from the
presented client surface. The horizon view looks tangent to the planet and
compares the upper two thirds. The outward views use controlled solar and night
inputs to isolate the disk and stars. RGB must match within one code value and
the bright solar centroid must remain within one pixel. Groundwork separately
qualifies scattering, limb and 1080p rebase in its D3D12 readback tests.
Reference-class GPU, CPU, memory and handoff
performance remain separate acceptance gates.

## Dawn and evening profile

`wyu.far-horizon.daylight.v1` evaluates the canonical local solar elevation.
Predawn and evening use the same continuous elevation curve. Low direct light
is warm, the ambient light retains a cooler component, and full daylight is
neutral. The fallback sky/fog colour follows the same phase. The dynamic sky
retains wavelength-dependent atmosphere scattering; the profile does not
paste a sunset panorama over the sky. Toposcope Sunset remains an offline
reference, with zero runtime HDRI texture bytes or draws.

Night ambient and background remain below daylight. Star visibility remains
separate from terrain illumination; catalogue stars, the Milky Way and nebulae
are still separate work in #79 and Groundwork #189. No new cloud, aurora or
local volumetric fog implementation is included here.

The existing 36,000-tick prototype solar cycle, saved environment tick and
weather-density recipe are unchanged. The daylight evaluator is stateless and
allocation-free. Existing Far Horizon saves adopt this visual profile on
Continue without losing their phase; no save-payload migration is introduced.
Ember Caves and the WP15 enable/disable policy are unchanged.

`eng/Test-ReferenceTwilight.ps1` renders 145 acknowledged presentations for
each of sunrise and sunset, with fixed canonical camera/view per sequence.
The probe samples the existing clock without modifying the accepted environment
tick or terrain publication. Empty engine barriers retain the normal bounded
receipt-retirement cadence without advancing that environment clock.
`-SunlightOff` checks the existing tint fallback. Optional `-OutputDirectory`
writes seven BMP/JSON checkpoints per
sequence. The capture-only fixture raises its own disposable window without
activation and restores its normal z-order afterwards; it rejects captures
whose client corners/centre belong to another process or lie outside the
desktop. Keep the desktop unobstructed throughout the run. Captures use the
physical client rectangle, including display scaling; the native render extent
is recorded separately by the renderer. They are visual
evidence, not GPU timestamps or display-latency measurements. The test operates
on a disposable smoke world, never on a player's save.

## Owner decisions for the paired PRs

| ID | Proposed candidate | Decision needed |
| --- | --- | --- |
| TW1 | A balanced warm horizon/direct sun with cooler ambient light; dawn and dusk mirror the same solar-elevation curve. | Approve the captured colour/intensity treatment or request a stronger/more restrained style. |
| TW2 | Preserve the existing 0.18 night-strength floor, with a cool colour bias rather than global exposure lift. The night stays darker than daytime. | Approve this readability level from the night captures; any later lower floor is a separate tuning change. |
| TW3 | Existing Far Horizon saves adopt the new appearance on Continue, retaining clock, seed and terrain. | Approve automatic visual adoption instead of maintaining a second legacy appearance profile. |

Implementation remains a candidate until these visual choices are reviewed.
See the [candidate capture set](dawn-twilight/README.md) for the concrete look
proposed by TW1/TW2 and its exact validation scope.
The WYU PR keeps the approved immutable engine pin. Paired validation may use
an explicitly identified isolated candidate renderer DLL, but that is not a
released-package result. Before final paired approval, merge/release the
approved Groundwork change through its existing policy, update all consumer
pins/digests/lockfiles together, and repeat the affected installed checks. Do
not relax the master-only producer or immutable-archive checks for a PR build.
