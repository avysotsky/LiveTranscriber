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
