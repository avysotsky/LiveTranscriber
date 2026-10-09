# LT-07 — ChatGPT subscription translation (Sign in with ChatGPT)

## Official integration

Use [OpenAI Sign in with ChatGPT for open-source tools](https://developers.openai.com/siwc/token-sharing-open-source), not ChatGPT browser cookies, internal `backend-api`, Plus passwords, or an ordinary billed API key. The app is open source and runs on the user's own Windows desktop.

- User explicitly chooses **Translator: ChatGPT plan (Sign in)**, presses **Continue with ChatGPT**, and approves use of available plan resources in their system browser.
- The app makes an initial dynamic-client registration request (`dynamic_agent_client`) with a per-computer `ext_agent_host_id`, a fresh PKCE S256 verifier/challenge, state and nonce, and a loopback `http://127.0.0.1:<port>/auth/callback` redirect.
- The returned issued `client_id` is used in authorization code exchange. Validate OIDC ID-token signature through the issuer's JWKS plus issuer, audience, expiration and nonce before accepting credentials. Granted scopes must include `resource.invoke` and `chatgpt.tokens.use.direct`.
- Access and rotating refresh tokens are **memory only** in this first implementation (authorization is required at each app restart); no persistence of credentials, ID tokens, transcripts or API responses.
- A stable random host identifier and the validated **nonsecret** issued client registration (client ID and opaque account subject) are saved under `%LOCALAPPDATA%\LiveTranscriber`. Returning sign-in reuses that registration. Neither file contains access/refresh tokens. **Disconnect** forgets both the in-memory credentials and the issued client registration.
- The app requests `GET https://api.openai.com/v1/models` with the authorized bearer token and displays the returned visible model list. It does **not** claim a hard-coded model is accessible to every Plus subscriber.
- Inference uses `POST https://api.openai.com/v1/responses` with `store=false`, `stream=true`; only the final successful `response.completed` event commits the accumulated Russian translation. A failed or incomplete response shows a safe translation error without recording English interview text.
- First-party **ChatGPT** OAuth authorization does not disclose prior ChatGPT conversations or memory to LiveTranscriber.
- Audio recognition uses local Sherpa-ONNX if Engine Local is selected; only *English interim and final text* is uploaded to OpenAI when translation is separately enabled. Groq and OPUS-MT options remain available.

## Windows usage

1. Update from `main` and launch LiveTranscriber as usual.
2. Set `Translator = ChatGPT plan (Sign in)`, click `Continue with ChatGPT`, and grant plan usage in the external browser. A refusal keeps the plan translator disconnected.
3. Wait for the model list and select one of the displayed models.
4. Enable `Auto-translate EN → RU`, then **Test translator**. The test submits only a fixed technical example, never interview speech.
5. When successful, select `Engine: Local (offline)`, the target capture application, and Start. English ASR remains the primary priority. Russian text may lag behind due to cloud network roundtrips, plan limits and other restrictions.
6. To disconnect without restarting, first Stop and click Disconnect. Credentials are also cleared on application exit.

## Boundaries and validation

The separate billed OpenAI API is **not** included in ChatGPT Plus by default. The specific *Sign in with ChatGPT* permission enables eligible requests under plan limits. This special flow is not an unlimited free API, cannot access ChatGPT history, and may be unavailable to some accounts/models. The app does not silently switch to another provider or incur conventional billed API calls with an API key.

The external sign-in requires a live user action; GitHub CI never exercises real OAuth against someone's account. Automated tests verify PKCE parameters, consent scopes, streaming completion/failure semantics and Windows build. Live account acceptance requires browser consent and one safe fixed-sentence test.

Official docs:
- https://developers.openai.com/siwc/token-sharing-open-source/sign-in
- https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference
- https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations
- https://openai.com/policies/sign-in-with-chatgpt-terms/


## LT-08 — incremental low-latency Russian translation

A field report found GPT translation was slow and seemed to wait until long speech phrases finished. Root cause in LT-07: a 4-second interim submission limit; a mandatory 2.5-second idle delay **after** each cloud request; and SSE `response.output_text.delta` events buffered until `response.completed` before the WPF panel received any Russian text.

Changes:

- GPT **interim translation** checks the latest English hypothesis on a dedicated 500ms UI timer (independent of 2s CPU telemetry), with a minimum 1.5s submission cadence while the translator is idle. The interim input is limited to the most recent 240 characters on a word boundary; large Sherpa utterances need not finish to be translated. Interim text is *provisional*, not an archival translation of every earlier word.
- **SSE streaming** now exposes cumulative Russian output as soon as the first output-text delta is available, with subsequent UI updates throttled to approximately 120ms or 32 more characters. The app displays this as a replaceable preview (ellipsis). Only a fully completed Responses stream is committed to final Russian text. Interrupted/failed streams clear partial output.
- The explicit, fixed 2.5-second post-request delay is reduced to 600ms **for ChatGPT plan only**. Groq keeps its existing 2.2s pacing; Local OPUS-MT remains unthrottled. ChatGPT preview is not queued while there is an active or pending translation, preventing stale backlog.
- There is **no bypass of plan rate limits**: HTTP 429 and errors are surfaced. Faster previews may use more of a plan's eligible request budget. English audio is never uploaded in the Local ASR mode and its processing is not blocked by the translator.

Tradeoffs: GPT still has server-side time-to-first-token latency, subject to chosen plan model and external network. Some speech endings and final sentences may be retranslated after temporary interim translations. Because Sherpa can revise an unfinished transcript, provisional Russian is deliberately replaceable; the latest 240-char preview window is not a comprehensive transcript. For a high-fidelity complete translation, wait for final recognized English segments. Test with real speech to measure subjective time-to-first-Russian and plan consumption.

## LT-09 — no blank Russian pane; durable live segments

A user observed that Russian text sometimes appeared within seconds, then vanished completely until a 10–15-second delayed batch arrived. Two root causes in the prior code:

1. `MainWindow.UpdateText` cleared `_previewRussian` immediately whenever Sherpa reported a **final English endpoint**, although the corresponding Russian translation could still be in flight. When earlier Russian was provisional, the pane became blank.
2. Every new long **interim** English hypothesis translated a trailing window and replaced the entire Russian preview. Already translated content from the same uninterrupted utterance was never committed until a final endpoint. The translation queue could also combine up to four finals, delaying and batching their visible output.

### New behavior

- ChatGPT mode uses `IncrementalEnglishChunker` to generate **ordered, non-overlapping English segments** during uninterrupted speech. A stable suffix (~12 characters) is retained before each interim segmentation, each chunk is limited to about 105 characters, and remaining text is flushed on the English final endpoint.
- ChatGPT translation queue uses `maxFinalBatch=1`: each chunk is translated and **committed to the durable Russian history** when the Responses SSE stream completes successfully, without waiting for the entire English utterance to end. Intermediate token deltas are displayed as provisional text.
- `RussianTranscriptBuffer` keeps all committed Russian segments visible. The most recent provisional result remains until a meaningful replacement is available; empty partials, an English endpoint, or a failed GPT stream **cannot clear the display**. Only explicit user **Clear** removes the history.
- A failed/incomplete GPT response may leave its last provisional text visible (clearly marked with an ellipsis), but never marks that text as committed. The failure status is shown separately. No automatic provider switch, no transcript logging, and the same ChatGPT plan permission and usage limits apply.
- The short GPT post-request pacing remains present to avoid instant tight retries; congestion leaves not-yet-submitted English chunks in a FIFO for subsequent dispatch, not discarded outright. This cannot guarantee real-world server TTFT or rule out longer network pauses, but it removes application-induced blanking and long-phrase batching.

This incremental commitment intentionally favors live readability over rewriting an entire previously translated long sentence. ASR may revise unfinished English; already committed Russian is not automatically retracted. That is a tradeoff for **never erasing successfully translated earlier content**. The final English transcript remains separately available for precision review.

Automated tests cover persistent preview, failure handling, segmentation during an ongoing utterance, exact source continuity, separate endpoints and backpressure. Actual OAuth/server latency still needs a field check.
