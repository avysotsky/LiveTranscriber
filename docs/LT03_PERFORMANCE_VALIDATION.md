# LT-03 — Resource-aware pipeline and on-device validation

## Why this exists

Development target: Intel Core i7-8850H, 6 physical cores / 12 logical processors, 16 GB RAM. Other applications may consume about half the machine's CPU resources. Preserving the conferencing application and speech clarity is more important than squeezing maximum ASR throughput.

## Current telemetry (in memory only)

The desktop app samples every 2 seconds:

- Application CPU utilization normalized to **total logical CPU capacity**; Windows Task Manager may use a different calculation or sampling interval.
- Application working-set memory and observed peak for this session.
- Current and peak **queued** audio duration, based on PCM mono at 16,000 samples/sec.
- Count and duration of **dropped** chunks, when the bounded queue is full.
- Client processing ratio (ASR processing time / processed-audio duration). This is LOCAL model compute RTF for offline recognition, but **NOT** cloud service latency in Cloud mode.

The health indicator reports `WarmingUp`, `Healthy` or `UnderPressure`. `UnderPressure` is immediate on fresh dropped audio, otherwise based on three consecutive 2-second samples above at least one threshold:

- App CPU >= 20%
- Backlogged audio >= 0.6 s
- Local ASR RTF >= 0.9 (local mode only)

Return to `Healthy` requires three consecutive clean samples. The thresholds are deliberately conservative **indicators**, not guaranteed resource budgets or automatic throttling.

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
