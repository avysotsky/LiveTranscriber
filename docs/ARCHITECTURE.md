# LiveTranscriber — Architecture and constraints

## Goal
Real-time English transcription of audio played by Windows applications, without capturing microphone input.

## Capture sources
- Device loopback (Windows WASAPI playback capture): initial implementation.
- Per-application loopback: separate planned source to exclude unrelated application sounds.

## Recognition providers
- Local streaming ASR via Sherpa-ONNX (CPU, compact quantized model).
- Cloud streaming ASR via Azure Speech (explicit opt-in, no audio upload in local mode).
- Provider switching must not require application restart.

## Hardware and resource limits
- Target machine: Intel Core i7-8850H (6 cores / 12 logical processors), 16 GB RAM.
- Assume approximately half of system capacity is already consumed by other processes.
- Initial local inference thread cap: two; benchmark under representative background load.
- Bound queues and track lost audio, transcript latency, process CPU, memory and real-time factor (RTF).
- Treat CPU/memory targets as measurements to verify, not guarantees.

## Reliability and privacy
- No microphone acquisition.
- Explicit start/stop capture; no automatic recording.
- Sensitive audio and transcripts must not be logged by default.
- Cloud recognition requires explicit enablement and credentials stored outside source control.
- Stop/replace recognition pipelines cleanly on device changes and provider switches.

## Roadmap
1. Compile and validate Windows playback capture plus a responsive desktop UI.
2. Integrate and benchmark local and cloud streaming recognition.
3. Add per-application capture and longer-run stress tests.
