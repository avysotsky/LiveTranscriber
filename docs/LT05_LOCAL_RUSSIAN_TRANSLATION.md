# LT-05 — local English → Russian translation

## Architecture

- The existing Sherpa-ONNX EN ASR remains in-process .NET.
- English final/interim ASR text enters the existing `TranslationPipeline`, independent of the audio ASR worker.
- With **Provider: Local OPUS-MT (offline)** the text is handed to a **persistent** local Python 3.11 subprocess by UTF-8 JSON lines over redirected stdin/stdout. A local CTranslate2 INT8 Marian translator is loaded only once per session.
- With **Provider: Groq (cloud)** the existing Groq text client remains available, and English interim/final text is sent to Groq only after the user explicitly chooses cloud provider and enables translation. No audio is uploaded by local translation; selecting Azure ASR separately uploads audio as before.
- The local worker uses `MarianTokenizer` saved alongside the converted model, and `HF_HUB_OFFLINE=1` / `TRANSFORMERS_OFFLINE=1`. There is no dynamic model download in LiveTranscriber.
- Preview Russian text is replaceable; finalized translations append in order. Both providers use the same queue, Clear/Stop behavior and bilingual UI.
- CPU resources are bounded with CTranslate2 `inter_threads=1, intra_threads=2` and INT8; this is a design choice, not an observed benchmark.
- The model is **not committed** to GitHub and is installed under D:\Models; the Python environment is ignored by git.

## Initial one-time installation (Windows PowerShell)

Requires Python 3.11 (the existing `py -3` command) and disk space for PyTorch *during conversion*; the download/conversion may be large.

```powershell
cd D:\Projects\LiveTranscriber
powershell -ExecutionPolicy Bypass -File tools\setup_local_translation.ps1
```

Setup creates `.venv-lt`, installs CPU CTranslate2, transformers, sentencepiece, sacremoses, and PyTorch (only needed for conversion), downloads Helsinki-NLP/opus-mt-en-ru, converts to INT8 and saves an offline tokenizer to `D:\Models\opus-mt-en-ru-ct2`.

After setup, in the PowerShell window where you launch the app:

```powershell
$env:LIVE_TRANSLATOR_MODEL_DIR = "D:\Models\opus-mt-en-ru-ct2"
$env:LIVE_TRANSLATOR_PYTHON = "D:\Projects\LiveTranscriber\.venv-lt\Scripts\python.exe"
$env:LIVE_TRANSCRIBER_MODEL_DIR = "D:\Models\sherpa-onnx-streaming-zipformer-en-2023-06-21"
dotnet run --project src/LiveTranscriber.Desktop -c Release
```

Check **Auto-translate EN → RU**, choose **Local OPUS-MT (offline)** (default), and press **Test translator** to translate a fixed sentence without uploading anything. Then Start with Engine Local and Capture Selected application.

**No API keys** needed in the offline path. After setup, Windows Defender Firewall may be used to block network access to the application and worker for verification; do not block the one-time installer while fetching model packages.

## Limitations / verification

- OPUS-MT is a dedicated MT model, not a context-rich LLM. Validate .NET, dependency injection and C# identifier handling on representative interview speech. The output may mistranslate identifiers, punctuation and unfinished ASR hypotheses.
- Preview updates are rate-limited to about one changed hypothesis every four seconds; translations run sequentially and do not block ASR.
- Local translation needs memory and CPU in addition to the 380 MiB/10% baseline observed **without** a local translator. No performance/latency guarantees before a real Windows run.
- Complete project test/build in CI **does not prove model installation or accurate native translation**. Use the in-app Test translator and real audio on the user's Windows host.
- Python worker protocol emits only generic errors and never logs transcripts/credentials. It is not an OS sandbox; choose appropriate protection for confidential interviews.
- The Python venv's installed dependencies and model files are trusted third-party components: obtain from verified package registries and official model URLs.
