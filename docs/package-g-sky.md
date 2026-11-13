# Package G · Far Horizon sky

Far Horizon uses the profile `wyu.far-horizon.atmosphere.v1`. The planet radius
is 4096 m and the shell is 112 m thick. Rayleigh and aerosol scale heights are
40 and 14 m. The Rayleigh scattering coefficients per metre are
(0.0012, 0.0026, 0.005) and the extinction coefficients are
(0.0014, 0.003, 0.006). Aerosol scattering and extinction use 0.002 and
0.0024 in each channel. Solar radiance is 16 and the disk radius is 0.009 rad.
The star seed is the XOR of the high and low 32-bit halves of the world seed.

The sky reads Package E's accepted clock and presentation fraction. The walking
camera, held terrain mask and paired terrain frame use canonical eye and view
data. The near and orbital flight renderers share the same flight camera and
projection angle. Local terrain fog continues to use Package E. Continue
reconstructs the sky from seed, pose and tick in the existing checkpoint.
Ember Caves retains its own presentation. Set `WYU_PACKAGE_G=off` to use the
clear-color fallback while retaining the Package E weather rules.

Run `eng/Test-ReferenceSky.ps1` with the pinned Release build. Repeat with
`-Adapter warp`. The fixture starts seeds 42 and 20261111 at tick 35500, checks
twilight, the first far presentation during ascent, orbit outside the
atmosphere, the first near presentation on return, and the landed sky after
Continue. Groundwork separately qualifies the scattering, stars, limb and
rebase pixels in its D3D12 readback tests. The flight replay checks presented
frames and atmosphere inputs. Reference-class GPU, CPU, memory and handoff
performance remain separate acceptance gates.
