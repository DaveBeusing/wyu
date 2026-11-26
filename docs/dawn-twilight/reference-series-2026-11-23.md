# X11D Direct and Lookup reference series, 23 November 2026

**CHANGES REQUIRED for integrated timing.** The standalone Groundwork Direct sky
fixture passed its measured gates. Both WYU compositions had failed comparison
rows in both modes. Lookup with retired storage reuse remains experimental.

## Evidence identity

- Groundwork `8943e96a73e82a8cd60db39fac87b28013869004` and WYU
  `bd59baaf9cc68743b12d4df48529a07f42ab4d90`. The runner stages both renderer
  assemblies over immutable Groundwork baseline `0.1.0-dev.45047410961.1`.
- X11D suite `GroundworkTests-20261123-070649-569cae28ba1e4fe7ad22ac17b728c631`.
  Direct ZIP `ReferenceResults-20261123-080650-194950830a7c406a9a80db6c7112abb7.zip`,
  SHA-256 `00EA9DFA0CF7CAF84055B2641A12A1B1C46DD34BEBE95FA05F86A0AC8D20AD29`.
  Lookup ZIP `ReferenceResults-20261123-100025-920ea82dd0784cb68bfb51b9ffa045f0.zip`,
  SHA-256 `F9BE24D3ABCFD759493BA2E7A3BF3C3A9D1A210E43928F5A183E1684FA7E3C32`.
- Direct synthetic fixture retained 60,000 samples. The four WYU
  mode/composition combinations each completed 60 flights and retained
  12,000 native frame timings, with 2,000 frames per arm in each pooled phase.
  Both archives report `FullQualification=false`.
- Direct and Lookup were rebuilt and measured in separate series. Their
  assembly hashes differ. Each toggle comparison is valid within its
  series. A controlled comparison between the two implementations would
  need matching binaries and a balanced execution order.

The [earlier full Lookup series](reference-series-2026-11-22-093150.md) and
the [focused Far Horizon ETW comparison](reference-focused-etw-2026-11-22.md)
remain attached to their original inputs. The latter attributed part of one
long draw to a context switch. The X11D series reported here uses the newer
paired heads and no scheduling trace.

## Measured comparisons

GPU frame entries are pooled p95 / p99 percentage changes. Handoffs are pooled
p95 changes. Limits are +5% / +8% for frame time and +10% for handoff.
The original per-pair limits also apply.

| Candidate | Surface | Ascent | Orbit | To orbit | To ground | Failed rows |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Direct, Far Horizon | +19.28% / +61.09% | −34.89% / −19.46% | +3.78% / +3.35% | −4.42% | −1.45% | 9/30 |
| Direct, Orbit Combined | +13.36% / +14.14% | +24.67% / +14.18% | +3.92% / +0.90% | +0.26% | +14.84% | 17/30 |
| Lookup, Far Horizon | +3.70% / +2.38% | −46.12% / −27.60% | +1.89% / −4.74% | +1.59% | −32.52% | 3/30 |
| Lookup, Orbit Combined | +6.90% / −1.09% | +3.31% / +7.88% | −1.06% / −0.13% | +1.08% | −26.68% | 8/30 |

Lookup Far Horizon failed Surface pairs 1 and 2 and Orbit pair 4. Lookup
Orbit Combined failed Surface pairs 1, 2 and 4 as well as the pooled row,
Ascent pairs 2 and 4, Orbit pair 5, and outbound handoff pair 1.
The Direct Orbit Combined return failures include pairs 3 and 4.

## Follow-up

Prioritize Orbit Combined Surface and the Direct return handoff. In the
Lookup Surface sample set, nearest-rank p95 after subtracting each enabled
sample's own `SkyMs` from its `FrameMs` is 0.813504 ms. Disabled full-frame
p95 is 0.775360 ms. The remainder alone is +4.92%, close to the +5% cap.
The enabled full-frame p95 is +6.90%. The enabled remainder already exceeds
the ceiling in Surface pairs 1, 2 and 4. In pair 4 it is 0.8611 ms against a
0.7436 ms ceiling. Compare the paired terrain and cascade timings before
changing the lookup shader. This subtraction is diagnostic and preserves
the original gate calculation.

The Direct Orbit Combined pooled Surface/Ascent frame overruns are
+13.36%/+24.67% at p95. The corresponding enabled frame remainders are
+1.61%/+0.11% versus the disabled full frames. Collect pass timestamps for
Direct and Lookup as separate comparisons to distinguish the Direct Sky
cost from the remaining Lookup workload. Correlate the Direct return
handoff stages to locate its delay. Full-series samples lack per-pass
terrain/cascade boundaries and a scheduling trace, so the cause of the
remaining variation is open.

On an idle X11D desktop, run the existing pinned companion in an elevated
PowerShell 7.4+ session. Verify the local copy against the pinned digest before execution. Both commands publish their evidence to the X11D share. The runner uses WYU 5b12324 and Groundwork 8943e96. Run these probes serially and retain the archive
from each. Check trace coverage for the affected windows before attributing
delays. Short probes can guide a correction; qualification still requires the
original complete protocol, installed route, pressure, memory and owner
visual evidence.

In the slowest Lookup outbound handoff of Orbit Combined pair 1, replay 4,
the endpoint grew from 35.852 to 40.242 ms. The interval from
`previous-stop-begin` to `previous-renderer-disposed` grew from 19.765 to
23.953 ms. The WYU follow-up adds opt-in `previous-props-stopped` and
`previous-renderer-dispose-begin` markers within that interval. Use these
with the existing `previous-renderer-disposed` marker to separate prop
shutdown, the stop request and disposal. The earlier archive predates both
markers, and the Direct return overrun still needs timing by stage.

```powershell
$work = Join-Path ([IO.Path]::GetTempPath()) ("groundwork-handoff-" + [guid]::NewGuid().ToString("N"))
$null = New-Item -ItemType Directory -Path $work
$runner = Join-Path $work "Invoke-Groundwork112-HandoffDiagnostics.ps1"
Copy-Item -LiteralPath "\\X11D\Share\Invoke-Groundwork112-HandoffDiagnostics.ps1" -Destination $runner
if ((Get-FileHash -LiteralPath $runner).Hash -ne "5864F2DA68D9C21F3A0E0A4E79564BB15DA6B0B7D50D8905DCDB4033E89A80BA") {
    throw "The handoff diagnostic runner does not match the reviewed SHA-256."
}
pwsh -NoProfile -File $runner -BaseDirectory "\\X11D\Share" -DiagnosticProbe -DiagnosticComposition orbit-combined -GpuPassBreakdown -GpuSchedulingTrace
pwsh -NoProfile -File $runner -BaseDirectory "\\X11D\Share" -DiagnosticProbe -DiagnosticComposition orbit-combined -LookupPrototype -ReuseLookupResources -GpuPassBreakdown -GpuSchedulingTrace
```
