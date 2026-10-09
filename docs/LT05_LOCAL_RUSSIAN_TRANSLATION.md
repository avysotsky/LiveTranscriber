# LT-05 — local English → Russian translation

## Architecture

- The existing Sherpa-ONNX EN ASR remains in-process .NET.
- English final/interim ASR text enters the existing `TranslationPipeline`, independent of the audio ASR worker.
- With **Provider: Local OPUS-MT (offline)** the text is handed to a **persistent** local Python 3.11 subprocess by UTF-8 JSON lines over redirected stdin/stdout. A local CTranslate2 INT8 Marian translator is loaded lazily and cached once per application window (not reloaded for every session).
- With **Provider: Groq (cloud)** the existing Groq text client remains available, and English interim/final text is sent to Groq only after the user explicitly chooses cloud provider and enables translation. No audio is uploaded by local translation; selecting Azure ASR separately uploads audio as before.
- The local worker uses `MarianTokenizer` saved alongside the converted model, and `HF_HUB_OFFLINE=1` / `TRANSFORMERS_OFFLINE=1`. There is no dynamic model download in LiveTranscriber.
- Preview Russian text is replaceable; finalized translations append in order. Both providers use the same queue, Clear/Stop behavior and bilingual UI.
- CPU resources are constrained with CTranslate2 `inter_threads=1, intra_threads=1`, `beam_size=1` and INT8, and the worker process is BelowNormal priority on Windows. This protects English recognition but trades some RU translation quality or peak throughput for lower CPU interference; measured impact must be confirmed on the real system.
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

## Recovering from an interrupted model conversion

If Python packages were installed, but the conversion printed
`RuntimeError: output directory ... already exists`, **do not remove your
Python virtual environment or reinstall the packages**. The model preparation
script is safe to rerun and repairs the dedicated incomplete model directory:

```powershell
cd D:\Projects\LiveTranscriber
& ".\.venv-lt\Scripts\python.exe" ".\tools\prepare_opus_mt.py" --output "D:\Models\opus-mt-en-ru-ct2"
```

The setup uses CTranslate2's documented `force=True` behavior for the
**dedicated offline model destination**, and skips reconversion once all
four required local artifacts are present. Do not use this destination for
unrelated personal files. Hugging Face's Windows symlink-cache warning only
indicates that the download cache may require more disk space; it is not
the cause of this conversion error.

## Limitations / verification

- OPUS-MT is a dedicated MT model, not a context-rich LLM. Validate .NET, dependency injection and C# identifier handling on representative interview speech. The output may mistranslate identifiers, punctuation and unfinished ASR hypotheses.
- Preview updates are rate-limited to about one changed hypothesis every four seconds; translations run sequentially and do not block ASR.
- Local translation needs memory and CPU in addition to the 380 MiB/10% baseline observed **without** a local translator. No performance/latency guarantees before a real Windows run.
- Complete project test/build in CI **does not prove model installation or accurate native translation**. Use the in-app Test translator and real audio on the user's Windows host.
- Python worker protocol emits only generic errors and never logs transcripts/credentials. It is not an OS sandbox; choose appropriate protection for confidential interviews.
- The Python venv's installed dependencies and model files are trusted third-party components: obtain from verified package registries and official model URLs.

## LT-06 — isolate offline translation from speech recognition startup

A field report after installing the OPUS-MT model says Russian text appears slowly and English ASR lags especially when translation is enabled. The previous `Start_Click` first called `await LocalOpusMtTranslator.StartAsync()`, *before* `TranscriptionSession.StartAsync()`. This forced English recognition to wait for cold Python module imports and model initialization. In addition, the previous CPU/RAM diagnostics measured only the WPF process and omitted the Python translator's resource consumption.

**Change:** pressing Start now creates a lightweight translation session proxy without loading OPUS-MT, starts the normal English audio pipeline immediately and initializes translation independently on its first pending phrase. Prefer pressing **Test translator** before a real interview: it preloads/warms the same translator instance which is reused on subsequent Start/Stop cycles until the WPF window closes. The very first auto-translation without prior warmup may still be late, but it must not be a prerequisite to starting English ASR.

The Python process gets BelowNormal priority on Windows and CTranslate2 decoding is greedy (`beam_size=1`) using a single inference thread; interim requests are skipped when audio is backing up or translation is already processing a previous phrase. Final English phrases are still queued. Translated text and audio never leave the device in Local/Local mode. Diagnostics now account for **desktop + ready local Python translator** CPU and working set; a cold worker's startup spikes before the ready signal are not necessarily captured in the reported peak.

**Validation:** Windows CI covers reusable model lifetime and start-time independence via deterministic stub workers. Since the repository does not include the ~78 MB converted model, the actual latency and CPU improvement must be confirmed by running Test translator and live English/Russian transcription on the user's PC. Keep the audio-first UX even if translation cannot initialize.
