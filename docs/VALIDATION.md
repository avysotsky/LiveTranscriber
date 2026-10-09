# LT-01 validation checklist

## Automated verification

GitHub Actions Windows CI builds the .NET 9 WPF desktop app and runs Core unit tests.

Automated green build validates compilation and isolated pipeline behavior. It **does not** prove audio capture, ASR accuracy, device compatibility, privacy policy compliance in the caller's conferencing client, or CPU utilization on the target machine.

## Required manual smoke tests on Windows

1. Start app without an ASR model in Local mode: expect a clear missing-model error and no audio capture.
2. Install the model using `LIVE_TRANSCRIBER_MODEL_DIR`, choose Local, play an English podcast into the *default output device*, and verify interim/final text.
3. Confirm the app never asks for microphone permissions or opens a microphone. Disable sidetone / Windows 'Listen to this device' if self-speech is played through output.
4. Stop and restart; switch from Local to Cloud only after providing Azure credentials and approving cloud transfer.
5. Verify an Azure failure is handled without crashing and that no audio / transcript is saved automatically.
6. Run 30 minutes with the typical background workload (~50% CPU busy): measure process CPU, working-set RAM, audio loss and latency. Do not assume performance targets have been met.
7. Change playback devices during capture; this currently requires stopping/restarting and must not be treated as supported automatic recovery.

## Framework support

This initial branch targets **.NET 9** for Visual Studio 2022 17.12+ compatibility. Microsoft ends .NET 9 and .NET 8 servicing on **2026-11-10**. This is temporary: plan LT-02/LT-03 retargeting to .NET 10 (LTS) and Visual Studio 2026, or use the .NET 10 CLI / toolchain. Do not ship a supported long-term release on .NET 9 after its end-of-support date.

Reference: https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/


## LT-01 real-device smoke test (user-reported, 2026-10-09)

- Machine: i7-8850H, 16 GB RAM, other workload occupying approximately half of the available computing capacity.
- Device WASAPI loopback + offline Sherpa-ONNX Zipformer INT8: real English playback was recognized.
- Subjective transcription quality: "almost no errors."
- LiveTranscriber process CPU observed: **12%**; application memory: **275 MB**.
- Sampling method, measurement interval, CPU peaks and end-to-end latency: **not measured**.
- This is preliminary observational evidence, NOT an acceptance benchmark for the application-capture backend or a 30-minute soak test.

## LT-02 process loopback verification (not yet performed)

1. Choose **Selected application** and the main visible browser / conferencing application window. Alternatively type the numeric PID.
2. Ensure Windows build supports process-loopback activation. Do not silently switch to full-device capture when it fails.
3. Play recognizable English speech in the selected app and unrelated audio in a separate process; only the selected process tree should be transcribed.
4. Repeat for Chrome / Edge multi-process setups, Teams and Zoom: if browser audio originates outside the selected process tree, select a different PID rather than assuming isolation.
5. Verify CPU, RAM, dropped chunks, local-processing RTF. Treat cloud upload ratio separately from local RTF.
6. Stop and restart capture, change process ID, exit selected application while capturing, and verify native resources are released.
