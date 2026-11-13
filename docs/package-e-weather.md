# Package E · Daylight and regional fog

Far Horizon advances its clock at accepted walking and flight steps. A day is
36,000 ticks at 60 Hz. The canonical camera position and the +X solar axis
determine tint and sky color. Fog fades between 32 and 72 metres above the
reference radius. The orbital renderer uses the same daylight and sky.

The recipe `wyu.reference-weather.v1` samples base elevation and meadow weight
at the centres of 256 m Cartesian regions. The cache retains at most sixteen
endpoints across the current and next 60-tick segments. Starting with the seed,
apply `Mix(value XOR input)` in order to the signed X, Y, Z region indices
as two's-complement 64-bit values, then the unsigned segment index. Mix is the
SplitMix64 finalizer with shifts 30, 27, 31 and multipliers
`0xbf58476d1ce4e5b9` and `0x94d049bb133111eb`. The high 24 bits provide noise.
At (128, 3968, −128), the opacity at ticks 0 and 60 is approximately
0.3540673 and 0.93092483 for seed 42, and 0.5552089 and 0.5875999 for seed 20261111.

Reference-world saves write WYUVOL03 with the environment tick, a landing flag,
and an optional landing record. WYUVOL01 and WYUVOL02 resume at tick 9,000.
Weather is reconstructed from seed, tick and pose. Ember Caves uses its existing
checkpoint payload and lighting.

## Windows checks

The gameplay tests cover both seeds, day phases, positive and negative region
boundaries, segment rollover, cache history, and altitude fade. The Windows
exploration and flight scripts check renderer handoff, landing and Continue.
The volume save and cave scripts exercise NTFS recovery and cave regression.

`eng/Measure-ReferenceWeather.ps1 -PresentMon <path-to-PresentMon.exe>`
runs paired NVIDIA replays with the same seed-42 spawn view, resolution and
biomes in both variants. Each process presents 420 frames in each of three
weather phases. The weather probe follows predefined canonical positions while
the camera view and terrain generation stay fixed. The script discards the first
pair, alternates order in five measured pairs, and exports frame and phase CSVs.
PresentMon GPU Busy comes from ETW GPU scheduling events. D3D12 timestamp-query
qualification is tracked separately.

Run the capture from an administrator PowerShell or an account in Performance
Log Users. The workstation build session could not create the ETW session.
