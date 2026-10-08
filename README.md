# LiveTranscriber

Windows **speaker-output-only** real-time English speech transcription in C# / .NET 9 and WPF.

- **Local:** Sherpa-ONNX streaming Zipformer (offline; INT8 encoder/joiner; two ONNX threads).
- **Cloud:** Azure Speech continuous recognition. Audio leaves the machine **only** after selecting Cloud and pressing Start.
- **Capture:** Windows WASAPI render-device loopback; the app never opens a microphone.
- **Audio:** downmix and stream resampling to mono 16 kHz, bounded queue (12 chunks), interim/final text.
- **Privacy:** no recording or transcript persistence; secrets only from process environment variables.

**MVP limitation:** Device loopback captures **all playback on the default Windows output device**, not only a remote participant. Microphone sidetone/monitoring replayed into the device will also be captured. Application loopback, playback-device selection, automatic fallback, and resource telemetry are future increments.

## Requirements

- Windows 10/11 x64; working output device.
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

Select Local or Cloud, press Start, and play English speech through the Windows default output device. The app shows partial hypotheses and finalized phrases; use Stop, Copy, Clear as needed.

## Resource constraints and verification

Target: Intel i7-8850H (6 cores / 12 logical processors), 16 GB RAM, **roughly half of compute capacity already busy**. Local recognition limits ONNX to two worker threads; incoming audio is queued with bounded capacity, dropping stale chunks when overloaded. This is not a guarantee of CPU less than 20% or latency below two seconds.

Before relying on the app for interviews, verify on Windows with a representative 30-minute call while usual workloads are running. Measure speech latency, dropped chunks, CPU and memory, and accuracy of .NET technical terminology.

## Roadmap

1. Application-specific loopback for Teams/Chrome with explicit process selection.
2. Playback device selector, recovery on device changes.
3. CPU/RTF telemetry, automated overload policy with opt-in Cloud fallback.
4. Resampling quality/performance benchmarking, timestamped transcript export, integration tests.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).