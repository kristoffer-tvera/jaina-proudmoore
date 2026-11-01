# /askjaina voice: survive the slow first DAVE handshake and drain short clips

The change reworks `VoicePlayback.PlayWavAsync` so the first `/askjaina` invocation survives Discord's now-mandatory DAVE E2EE voice path. Two fixes land together: `ConnectAsync` is wrapped in a 3-attempt retry (`ConnectWithRetryAsync`) so the slow initial MLS handshake that trips Discord.Net's connect timeout gets a warm retry, and the PCM stream is created with `bufferMillis: 40` instead of the 1000ms default so a short TTS clip actually crosses the transmit preload threshold instead of sitting silent while `FlushAsync` spins forever. Playback now runs on its own 2-minute timeout token linked to — but distinct from — the worker `_stoppingToken`, flush is bounded to 10s, and the PCM stream is disposed before `StopAsync` so teardown stops wedging on "Disconnecting". Only `VoicePlayback.cs` changed; the DAVE setup, Dockerfile, csproj (net10.0, Discord.Net 3.20.2), and all other commands are untouched.

Watch for: the retry only helps if the first-connect cancellation actually surfaces as an exception out of `ConnectAsync` rather than only on the ConnectionManager background task behind a successfully-returned client (likely, but not traced through Discord.Net internals — possible). `ConnectAsync` takes no CancellationToken, so a hung connect attempt is not itself interruptible (confirmed). `pcm.DisposeAsync()` in the teardown path is not timeout-bounded (confirmed).

**Verdict**: APPROVED

## High-level view

The connect fix retries `ConnectAsync` up to three times with a 500ms gap, logging each failure, and gives up only after the third. This is the right shape for a transient first-handshake failure that a warm second attempt clears — which is exactly the behavior the user reported manually. The one load-bearing assumption is that the observed `TaskCanceledException` propagates out of the awaited `ConnectAsync` so the `catch` can see it. If Discord.Net instead returns a live `IAudioClient` and raises the cancellation only on its internal connection task (the log shows "Connected" before the exception), the retry never fires and the first call still dies. The commit treats the throw-to-caller path as given; it is the plausible path but was not confirmed against the library.

The silent-playback fix rests on a confirmed API fact: `CreatePCMStream`'s `bufferMillis` defaults to 1000, and the buffered writer only begins transmitting after preloading `bufferMillis/20ms` frames (50 frames ≈ 1s), so a sub-second answer never plays and the flush never completes. Dropping to 40ms (2 frames) lets any clip preload and drain. This matches the reported "silent + hung on Disconnecting" symptom and the referenced issue #687.

The token story is sound. Playback gets a linked token with a 2-minute cap so a worker-lifetime blip can no longer cancel audio mid-stream, while shutdown still propagates through the link. Teardown (ffmpeg wait/kill) deliberately uses the original `ct`, not the playback token, so cleanup runs even after playback is cancelled. Flush is bounded by a 10s linked token and the cancellation is caught, so a stalled encrypt pipeline can no longer hang the leave.

The teardown ordering — dispose the PCM stream, then `StopAsync`, then dispose the client — is the correct sequence for releasing the buffered writer before leaving. The residual gap is that `DisposeAsync` on the PCM stream is the one teardown step with no timeout; if disposal itself blocks on the undrained queue rather than cancelling it, the "Disconnecting" hang could reappear through that path.

<details>
<summary>Issues (3)</summary>

1. **Connect-cancellation propagation unverified** — The retry fixes the first connect only if the cancellation throws out of `await ConnectAsync`. The log shows "Connected" logged before the `TaskCanceledException`, which leaves open the possibility that `ConnectAsync` returned a client and the cancel fired on the background connection task (never caught by the retry). Confirm against Discord.Net 3.20.2 that a connect-timeout cancellation surfaces to the `ConnectAsync` awaiter; if not, also handle the `IAudioClient.Disconnected`/connection-state path.
2. **`ConnectAsync` is not cancellation-bounded** — `ConnectAsync` takes no CancellationToken, so `playbackToken` only gates the loop between attempts, not a single hung attempt. A connect that hangs (rather than cancels) will not be interrupted by the 2-minute playback cap. Acceptable if Discord.Net self-cancels connects, but worth noting.
3. **`pcm.DisposeAsync()` unbounded in teardown** — Every other teardown step is timeout-bounded, but the PCM disposal that is supposed to release the writer is not. If disposal drains rather than cancels the queue, the "Disconnecting" hang can return via dispose. Consider bounding it or confirming disposal cancels rather than flushes.

</details>

<details>
<summary>Details</summary>

### Retry as the first-connect fix

The exception filter `attempt < MaxConnectAttempts && !ct.IsCancellationRequested` is worth noting: a real cancellation breaks out rather than being swallowed and retried as a connect failure, and the final attempt's exception propagates uncaught instead of silently returning no client. The load-bearing assumption is that the first-connect cancellation reaches this `catch`. The user's log sequence is "Connected" → `TaskCanceledException` at `Discord.ConnectionManager.StartAsync` (surfaced via the Log event) → "Disconnecting" → "Disconnected". `IAudioChannel.ConnectAsync` is confirmed to take no CancellationToken and to return an `IAudioClient`. If the awaited connect faults with that `TaskCanceledException`, the retry catches it and the warm second attempt lands — fix works. If instead Discord.Net completes `ConnectAsync` with a live client and only the internal connection task is cancelled afterward, `ConnectWithRetryAsync` returns a doomed client and the subsequent `CreatePCMStream`/`CopyToAsync` fail against a disconnected transport, with no retry. The log showing "Connected" *before* the exception is the reason this can't be dismissed. The behavior was not traced through the library, so this is the one place the connect fix could miss.

### Small buffer as the silent-playback fix

Confirmed by reflection on Discord.Net.Core 3.20.2: `CreatePCMStream(AudioApplication application, int? bitrate = null, int bufferMillis = 1000, int packetLoss = 30)`. The named `bufferMillis: 40` argument is valid and the default really is 1000. With the documented preload rule (transmit starts after `bufferMillis/20ms` frames), the default needs ~50 frames (~1s) before a single packet goes out, so a short Gemini answer is both never transmitted and never drainable — the exact "silent + hung" pairing. 40ms drops the threshold to 2 frames, which any clip clears. The reasoning holds and matches the cited issue #687.

### Playback token separate from worker lifetime

```
ct (_stoppingToken, worker lifetime)
  └─ playbackCts = CreateLinkedTokenSource(ct), CancelAfter(2 min)
       └─ playbackToken  → ConnectWithRetry, ffmpeg stdin write, CopyToAsync
            └─ flushCts = CreateLinkedTokenSource(playbackToken), CancelAfter(10s)
                 └─ flushCts.Token → pcm.FlushAsync
```

The one subtlety the diagram makes concrete: teardown's ffmpeg wait/kill intentionally uses the original `ct`, not `playbackToken`, so cleanup still runs after the playback token has been cancelled or hit its 2-minute cap. That asymmetry is deliberate — it is what keeps a cancelled playback from also cancelling its own teardown.

### Flush and teardown ordering

Flush is wrapped in a 10s linked token; on timeout the `OperationCanceledException` is caught and logged ("tearing the connection down anyway") rather than propagated, so a stalled encrypt/encode pipeline no longer blocks the leave. The `finally` then disposes the PCM stream before `StopAsync`, which is the correct order: releasing the buffered writer first leaves the connection idle so `StopAsync` performs a clean leave, and the client is disposed last. This directly targets the "Disconnecting" hang.

The remaining gap is that `pcm.DisposeAsync()` is the only teardown step without a timeout. The fix depends on disposal *cancelling* the buffered writer rather than draining it; if disposal instead blocks on the same undrained queue that `FlushAsync` spins on, the hang simply moves from flush to dispose. Everything else in teardown is bounded, so bounding this (or confirming dispose cancels) would close the loop.

### Preserved behavior

ffmpeg transcoding is intact — still `-i pipe:0 -ac 2 -f s16le -ar 48000 pipe:1`, 24kHz mono WAV to 48kHz stereo s16le, no text-only fallback. The reader/writer split (one task pushing WAV into stdin and closing it for EOF, one `CopyToAsync` draining stdout to the PCM stream) is unchanged in structure. The DAVE setup is fully untouched: `EnableVoiceDaveEncryption = true` in Worker.cs and the libdave fetch/symlink logic in the Dockerfile are outside the diff. The role toggle, `/yoink`, and message auditing live in Worker.cs, which is unchanged. csproj stays net10.0 with Discord.Net 3.20.2 — the commit's rationale that no released version fixes this and the relevant PR #1580 is unreleased is a reasonable basis for not bumping.

### Build / warnings

Per the commit message, `dotnet build JainaProudmoore.csproj -c Release` succeeded warning-clean under `-warnaserror` (the flag is applied via the CLI build command per README, not a csproj property). Not re-run per instructions. The retained non-null `ffmpeg`/`proc` local pattern and the comment about CS8602 indicate the nullable-under-warnaserror constraint was kept in mind.

</details>

<details>
<summary>File map</summary>

- `JainaProudmoore/VoicePlayback.cs` — connect retry (`ConnectWithRetryAsync`), `bufferMillis: 40` PCM stream, playback/flush timeout tokens, dispose-before-stop teardown ordering. Only file changed.

Full diff: `git diff b742b64 HEAD -- JainaProudmoore/VoicePlayback.cs`

</details>
