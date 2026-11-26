# Far Horizon ascent GPU scheduling comparison

The 22 November X11D capture compares only the Groundwork sky lookup shader.
Both arms enable sky, experimental lookup and retired-resource reuse. The
baseline renderer is `e367d50f77c8d80d76cdf64fd0d2be3109fd7a12` and the
footprint candidate is `d67d8d6e1d18dbeb182c5d807d78e9272292798c`. The four
Far Horizon flights ran in ABBA order on an i9-10850K and RTX 5070 with driver
`32.0.16.1088`. Each recorded 400 native samples per phase.

| Flight | Ascent draw p95 / p99 (µs) | Ascent frame p95 / p99 (µs) | Draws over 150 µs | Draws over 150 µs with an observed graphics context switch |
| --- | ---: | ---: | ---: | ---: |
| Baseline 1 | 50.816 / 236.896 | 625.600 / 800.768 | 8 | 0 |
| Footprint 1 | 47.840 / 270.528 | 571.616 / 756.640 | 10 | 0 |
| Footprint 2 | 45.696 / 46.848 | 569.792 / 701.216 | 0 | 0 |
| Baseline 2 | 49.664 / 50.080 | 802.240 / 816.544 | 1 | 1 |

The candidate lowers draw p95 in both flights, and long draws occur in both
variants. Package G's relative frame-time and handoff gates require a matched
sky-disabled control.

## Scheduling evidence

Join the 1,600 Ascent CSV rows to `gpu-pass-ticks` by owner, sequence, fence
and phase. The recorded draw durations reconcile within 0.11 µs. Decode
the raw DxgKrnl ETL payloads with Windows ProcessTrace using raw timestamps.
The ETL reader reported zero lost events and buffers. Event 432 scheduling
logs cover the graphics node/engine 0/0 for 1,597 of 1,600 ascent draw
windows and all 19 outlier windows. Check clock calibration before comparing
the scheduler GPU ticks with the draw timestamps. Match client graphics
contexts to each flight's process ID using context creation and scheduler
object associations (events 30 and 433).

One 530.080 µs baseline draw (sequence 640, fence 1279) contained six
scheduler state records for three graphics context switches. The client context switched out 84.352 µs after draw
start and back in at 433.345 µs, a 348.993 µs gap or 65.8% of that draw.
The other 18 outliers contained no decoded transition. Each of the two affected
flights had a consistent sequence offset modulo three. Their outlier start
times were separated by multiples of about 40 ms within 0.237 ms. After clock
alignment, no decoded DxgKrnl GPU preemption-attempt timestamp (event 22)
fell within an outlier draw or the 1 ms on either side. The trace describes
the captured graphics engine and the recorded intervals. The reason for the
recurring outliers remains open.
All reported timings retain their original samples and endpoints.

## Raw capture

The capture is under
`\\X11D\Share\Groundwork-193-ETW-RTX5070-20261122-210353-65854483`.
Reconcile the native `stdout.log`, `ascent.csv`, `flight-process.json`
and `scheduling` ETL in each flight directory. The same client DLL was used
for all flights (SHA-256
`D191F0F326B78C9EC3236C4FA0F632870CC9EC2E2EBE528C047554DCB544F9AE`).

| Artifact | SHA-256 |
| --- | --- |
| `manifest.json` | `B176574B2D523C35F6258A33DD5A1F7E539599EF5D54B85A053D5C11061CCB23` |
| `comparison.csv` | `AE2DA2E43584C7FCF68AD3A25C1430A1FA3A3569C8B185400009AB8975702227` |
| `runtime-sha256.json` | `CFC0B223D62DCB27E2BEFE53BC9448B33910C18D1E172BAC3AB314DAECDC8D57` |
| Baseline 1 ETL | `1C1DC75F4CFC2285814203F8F8FA45EC53730095B296134004D59B208D81D365` |
| Footprint 1 ETL | `5F1DC80B6DE7C354A883A46D1782AF09F704461F3382986D6B23A06778BD480A` |
| Footprint 2 ETL | `34E0AA812F9B18C3B12493FC05063C766CEE652DE48032CA406EDC30D2B06131` |
| Baseline 2 ETL | `30F8BFB58AFC79F121090F6887D0EEF070B4E3F3E056FF9E79E1E2BEE72F64D6` |
