# WP13 · Biomes from ground to orbit

Far Horizon uses the visual recipe `wyu.reference-biomes.v1`. The seed rotates and tilts a band axis in canonical planet space. Terrain relief is represented in the same coordinates as the mesh. The table blends meadow, steppe and highland using quantized smooth weights that sum to 255. Tie order is meadow, steppe, highland. Basalt and ochre share the palette, with a red-channel tint for ochre. The recipe and its shared 1,024-entry lookup table are derived at runtime. WP12 Ember Caves uses its geology palette.

The near upload callback and the orbital patch builder call the same color evaluator. Camera and local render origin are outside its inputs. Each evaluation allocates zero managed bytes after initialization. The existing vertex buffers, upload path, draw topology and orbit landing marker carry the colors.

## Windows checks

The gameplay regression compares seeds 42 and 20261111 at 486 canonical control points per seed, including 92 points shared by cube charts. It checks exact normalized weights, three dominant zones, blended samples, distinct seeds and reversed parallel replay.

Run `pwsh -File eng/Measure-BiomeRendering.ps1` after the verified consumer build. It uses seed 42 for paired NVIDIA walking and flight replays, warms up each mode, and writes raw logs and CSVs. The replays record host submission through observed Present, CPU staging, globe build and renderer handoff. The measurement build disables the Debug Layer in both modes to keep the long replay below its diagnostic message cap. Normal graphics smoke tests use Debug Layer and DRED.

The renderer prepares the fixed color callback when opening a reference planet and reuses it after a handoff. After the first far frame, the flight stages its return terrain within the streaming budget. The CPU build runs privately against the held return view and the preflight terrain revision. At the handoff, the near renderer binds that view, submits the neutral mask and publishes the prepared terrain through the normal native and Core gates. Aborting cancels and joins the private build before host teardown.

The benchmark logs QPC intervals by process and managed allocation bytes on the synchronous near-pack and globe-build threads. The allocation totals include returned buffers, so they are conservative bounds for the managed temporary memory of each build. For GPU measurements without a tracing session, sample the Windows GPU Engine running-time counters for the replay process and intersect their intervals with the QPC phase windows. These counters give sampled engine busy time. Short boundary intervals and per-frame GPU percentiles require a finer trace.
