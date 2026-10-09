# LiveTranscriber

Windows **speaker-output-only** real-time English speech transcription in C# / .NET 9 and WPF.

- **Local:** Sherpa-ONNX streaming Zipformer (offline; INT8 encoder/joiner; two ONNX threads).
- **Cloud:** Azure Speech continuous recognition. Audio leaves the machine **only** after selecting Cloud and pressing Start.
- **Capture:** Windows WASAPI render-device loopback **or** selected application process-tree loopback; the app never opens a microphone.
- **Audio:** downmix and stream resampling to mono 16 kHz, bounded queue (12 chunks), interim/final text.
- **Privacy:** no recording or transcript persistence; secrets only from process environment variables.

**Capture scopes:** "All speaker output" captures the entire default output device; "Selected application" captures a selected PID and child processes. This does not capture the microphone directly, but microphone monitoring/echo replayed through the selected audio stream may still appear. No silent fallback from app capture to device capture is allowed. Device selection and automatic provider fallback remain future increments.

## Requirements

- Windows 10/11 x64; working output device. App loopback needs at least Windows 10 version 2004 (build 19041) for the NAudio 3 backend; actual driver/OS support must be tested. Microsoft's sample documents a more conservative build 20348 baseline.
- Visual Studio 2022 17.12+ and **.NET 9 SDK**, or .NET 9 CLI.
- Local streaming model for Local mode, Azure AI Speech resource for Cloud mode.

## Local setup

Download the official [English streaming Zipformer model (2023-06-21)](https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-streaming-zipformer-en-2023-06-21.tar.bz2).

Extract the model folder; it must contain:
- `tokens.txt`
- `encoder-epoch-99-avg-1.int8.onnx`
- `decoder-epoch-99-avg-1.onnx`
- `joiner-epoch-99-avg-1.int8.onnx`

In PowerShell before launching the application:

```powershell
$env:LIVE_TRANSCRIBER_MODEL_DIR='D:\Models\sherpa-onnx-streaming-zipformer-en-2023-06-21'
```

Without the environment variable, the app looks for `models/sherpa-onnx-streaming-zipformer-en-2023-06-21` under its output folder. Do not commit the model files.

## Cloud setup

Provide an Azure Speech key/region to the process:

```powershell
$env:AZURE_SPEECH_KEY='YOUR_SECRET_KEY'
$env:AZURE_SPEECH_REGION='YOUR_REGION'
```

Azure may incur usage charges. Do not commit or log keys. Cloud transmits audio only after explicit opt-in by selecting Cloud and clicking Start. Automatic cloud fallback is **disabled**.

## Build, test, run

```powershell
dotnet restore LiveTranscriber.sln
dotnet test LiveTranscriber.sln -c Release
dotnet run --project src/LiveTranscriber.Desktop/LiveTranscriber.Desktop.csproj -c Release
```

Select Local or Cloud. For source, choose **All speaker output** (works like LT-01) or **Selected application** and select its visible window. You can also type a numeric PID in the process selector. Press Refresh to update the process list. Some browsers have multiple processes: choose the main conferencing window and verify that its child audio renderer belongs to that process tree. There is no silent fallback to whole-device audio if app capture fails. Press Start; the app shows partial and finalized phrases. Use Stop, Copy, Clear.

The bottom of the UI reports **process CPU share, working-set RAM and session peaks; queued audio duration; audio-loss seconds; average/peak capture callback-to-ASR queue wait; and client processing ratio**. In Local mode the ratio is local inference RTF; in Cloud mode it measures upload/write time only, **not** service latency. After pressing Stop, use **Copy diagnostics** to copy a privacy-safe session report (no speech or transcript text). Counters are otherwise kept only in memory. **Queue wait is not the end-to-end speech-to-transcript delay**: Windows buffering, transcription endpointing and UI rendering are not included.

The health indicator warns on sustained high CPU (20%+), **queue occupancy (at least 9 of 12 chunks)**, local RTF (0.9+) or any new dropped audio. A short queue fill to 9 slots is recorded as a **near-capacity event** even if it drains before the next UI update; this is a warning before potential loss, not evidence of actual dropped audio. Three clean samples are required for recovery. **It does not automatically upload audio or switch providers.** See [LT-03 performance acceptance plan](docs/LT03_PERFORMANCE_VALIDATION.md) for the 30-minute test procedure.

## Resource constraints and verification

Target: Intel i7-8850H (6 cores / 12 logical processors), 16 GB RAM, **roughly half of compute capacity already busy**. Local recognition limits ONNX to two worker threads; incoming audio is queued with bounded capacity, dropping stale chunks when overloaded. This is not a guarantee of CPU less than 20% or latency below two seconds.

Before relying on the app for interviews, verify on Windows with a representative 30-minute call while usual workloads are running. Measure speech latency, dropped chunks, CPU and memory, and accuracy of .NET technical terminology.

## Roadmap

1. Validate app-specific loopback with real Teams/Chrome/Zoom processes on user Windows build; parent/child-process boundaries and zero-audio cases vary.
2. Playback device selector, recovery on device changes and selected-process exits.
3. Adaptive overload policy with **opt-in** Cloud fallback.
4. Resampling quality/performance benchmarking, timestamped transcript export and 30-minute soak tests.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).