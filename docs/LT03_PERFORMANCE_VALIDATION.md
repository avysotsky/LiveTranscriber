# LT-03 — Resource-aware pipeline and on-device validation

## Why this exists

Development target: Intel Core i7-8850H, 6 physical cores / 12 logical processors, 16 GB RAM. Other applications may consume about half the machine's CPU resources. Preserving the conferencing application and speech clarity is more important than squeezing maximum ASR throughput.

## Current telemetry (in memory only)

The desktop app samples every 2 seconds:

- Application CPU utilization normalized to **total logical CPU capacity**; Windows Task Manager may use a different calculation or sampling interval.
- Application working-set memory and observed peak for this session.
- Current and peak **queued** audio duration, based on PCM mono at 16,000 samples/sec, and occupancy in **chunks** out of the fixed 24-slot queue.
- Count and duration of **dropped** chunks, when the bounded queue is full.
- Client processing ratio (ASR processing time / processed-audio duration). This is LOCAL model compute RTF for offline recognition, but **NOT** cloud service latency in Cloud mode.
- **Mean and maximum queue wait** from each source callback into the start of its ASR processing call, plus the longest individual client-side recognizer call. These are sampled using monotonic Stopwatch ticks; they are NOT speech onset-to-rendered-transcript latency.

The health indicator reports `WarmingUp`, `Healthy` or `UnderPressure`. `UnderPressure` is immediate on fresh dropped audio, otherwise based on three consecutive 2-second samples above at least one threshold:

- App CPU >= 20%
- At least 75% occupied queue slots (18 of 24), regardless of each packet's duration
- A new near-capacity crossing event since the previous observation, even if the queue already drained (alerts immediately)
- Local ASR RTF >= 0.9 (local mode only)

Crossing eighteen queue slots is recorded within the capture pipeline rather than only sampled on the UI's 2-second timer. The crossing counter therefore detects brief backlog spikes that would be missed by a periodic current-queue sample. A new crossing produces an immediate warning at the next UI sample; this does not imply audio was lost. Return to `Healthy` requires three consecutive clean samples. The thresholds are deliberately conservative **indicators**, not guaranteed resource budgets or automatic throttling.

**Privacy:** these counters contain no audio or transcript contents. The `Copy diagnostics` button copies the on-screen session report to the clipboard but does not write a file. Cloud fallback is **not automatic**, to avoid unexpected audio upload.

## 30-minute acceptance run (user's Windows computer)

1. Update GitHub `main`, run unit tests, and start the application under the same background tasks normally active during an interview.
2. Start **Local**, choose **Selected application**, choose the conferencing/video process, and play English speech continuously or in bursts for 30 minutes.
3. Add a separate application that plays unrelated audio; verify that the selected process tree, rather than all system playback, is captured. Browser multi-process handling requires manual verification.
4. Observe peak process CPU/RAM, local processing RTF, queued audio, lost audio, dropped chunk count, UI responsiveness, and text delay.
5. Press Stop, then `Copy diagnostics` and paste the report into the GitHub issue or development chat **after checking that it contains no unwanted metadata**.
6. Repeat with Cloud mode only when explicitly authorized and configured. Cloud ratio measures *client upload work*, not server speech latency.
7. Test Stop/Start and change capture PID; confirm no microphone is opened.

**Initial user observations (LT-01):** CPU approx 12%, RAM approx 275 MB, nearly error-free English recognition. These were informal point measurements, not a timed soak or measurement of maximum delay.

## Acceptance and exclusions

This increment improves observability and applies a deterministic queue-drop policy when the inference worker falls behind. It does NOT implement automatic model changes, automatic Azure fallback, prompt/answer generation, or end-to-end speech latency instrumentation. These remain separate work items pending measured evidence and explicit opt-in as necessary.

Do not assert that the CPU remains under 20%, RTF remains below 0.5, that zero audio drops occur, or that recognition is fully private in Cloud mode without testing the relevant path.

## 2026-10-09 user-reported field run (LT-03a)

Source: Local INT8, **Selected application**, duration **00:17:39**. The user reports that the capture/recognition workflow is working normally.

| Metric | Value |
| --- | ---: |
| Peak process CPU | 11.2% |
| Peak working set | 386 MiB |
| Local processing ratio | 0.12 |
| Peak queued audio | 0.12 s |
| Remaining queue at stop | 0.00 s |
| Dropped audio | 0.15 s (15 chunks) |
| Processed audio | 1058.86 s (105886 chunks) |
| Final health | Healthy |

The lost fraction is approximately 0.014% of processed audio. **15 lost chunks are not zero loss**; a brief missed syllable is possible. All reported lost chunks have an average duration of ~10ms. With 12 queue slots, a 0.6-second queue threshold was ineffective for the observed 10ms packet size; LT-03b corrects this by counting buffer slots and threshold crossings. The observed 11.2% process CPU, 386 MiB RAM and 0.12 local processing ratio support resource feasibility for this shorter run, but do not guarantee 30-minute behavior. Speech-to-text latency was not measured. The user has not yet supplied a separate-app isolation result, so do not declare isolation verified by this run alone.

The next full acceptance run remains 30 minutes with representative competing workloads.

## 2026-10-09 user-reported follow-up (LT-03b)

Source: Local INT8, **Selected application**, session length **00:11:10**, after queue occupancy instrumentation.

| Metric | Value |
| --- | ---: |
| Peak process CPU | 9.7% |
| Peak process working set | 386 MiB |
| Local processing ratio | 0.11 |
| Peak queued audio | 0.09 s (9/12 chunks) |
| Near-capacity events | 1 |
| Dropped audio | **0.00 s (0 chunks)** |
| Processed audio | 670.66 s (67066 chunks) |
| Final health | Healthy |

**Interpretation:** zero observed dropped audio for the 11-minute run, one transient queue watermark crossing with no lost packets, and low steady ASR processing cost. Healthy is the *final* state; short warnings during the run are possible and not reproduced in the report. Does not certify 30-minute resilience or spoken-word latency. The longest queue wait can be estimated after LT-03c, but cannot establish speech-to-text latency without a timestamped word-level reference.

## LT-03c capture-to-ASR instrumentation

Each received audio frame is timestamped with a monotonic clock at its **capture callback**. When a frame is dequeued, the app measures the time until ASR processing **starts**. The mean and peak capture callback-to-ASR-start times and longest individual recognizer call are reported in **milliseconds**. These metrics help distinguish queue contention from ASR computation without recording or logging audio.

They explicitly exclude upstream Windows audio-capture buffering, model endpoint segmentation, UI dispatch/rendering and any network/server ASR delay. A value such as 2 ms queue wait does **not** mean that the transcript appeared 2 ms after the speech.

## 2026-10-09 field report: ~30-minute LT-03c run

Source: Local INT8, Selected application. Reported elapsed time **00:29:49** (11 seconds short of exactly 30 minutes).

| Metric | Value |
| --- | ---: |
| Peak CPU | 10.0% |
| Peak working set | 380 MiB |
| Local processing ratio | 0.12 |
| Peak queued audio | 0.12 s (12/12 frames) |
| Near-capacity crossings | 7 |
| Dropped audio | 0.01 s (1 frame) |
| Processed audio | 1789.26 s (178926 frames) |
| Mean capture callback-to-ASR start wait | 1.33 ms |
| Peak capture callback-to-ASR start wait | 105.27 ms |
| Longest ASR call | 135.43 ms |
| Final health | Healthy |

One dropped ~10ms packet represents about **0.00056%** of the observed 1789.27s input. The zero-loss criterion was not strictly met, although CPU, memory, and average queue wait remain comfortably within targets. The queue filled to 12/12 slots while the longest client-side ASR call was 135ms. Both facts indicate that additional burst capacity may be useful, but the summary report does not prove the drop occurred during that particular ASR call.

## LT-03d targeted mitigation

Increase the bounded audio packet queue from **12 to 24 slots**, shifting the 75% high-watermark alert from 9 to **18 slots**. At the observed typical 10ms packet size this corresponds to approximately 240ms of queue headroom, accommodating one roughly 135ms processing stall without necessarily dropping a sample. The queue remains bounded and retains the oldest-frame eviction policy when full. No extra ASR threads, no microphone capture, no automatic cloud fallback. Larger queued blocks (if returned by a different audio device or source) can represent proportionally longer buffered audio; neither a hard 240ms latency ceiling nor zero-loss guarantee is claimed.

**Acceptance to repeat:** another 30-minute session under comparable background load; prioritize zero dropped packets, observe peak callback-to-ASR wait and near-capacity crossings. Include separate-application isolation test if not yet performed. Do not infer full speech-to-screen latency from the queue-wait measurement.
