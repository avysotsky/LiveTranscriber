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
