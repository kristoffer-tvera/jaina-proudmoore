# /askjaina voice: prove the connection stable before playback instead of retrying ConnectAsync

The second pass abandons the previous retry-around-`ConnectAsync` design and targets the actual failure mode. `ConnectWithRetryAsync` is replaced by `ConnectStableAsync`, which connects, subscribes to `IAudioClient.Disconnected`, then waits a 1.5s settle window. A connection that stays `Connected` through the window is handed to playback wrapped in a `StableConnection` record (client + a `DisconnectedToken` + a `Release` callback); a connection that drops inside the window is torn down and reconnected, up to four attempts. The `DisconnectedToken` is linked into the playback stream so a mid-stream drop cancels the write/copy chain instead of writing into a dead transport. The `bufferMillis:40` PCM fix, ffmpeg transcoding, playback/flush timeout tokens, and dispose-before-`StopAsync` teardown ordering are preserved. Only `VoicePlayback.cs` changed.

This is the correct structural fix for the confirmed diagnosis. The previous pass depended on a `TaskCanceledException` throwing out of the awaited `ConnectAsync`; the live run proved it never does (no "attempt 2" log) because the cancellation fires on Discord.Net's internal `ConnectionManager` background task *after* `ConnectAsync` has already returned a live client. The new design never relies on that throw — it observes the only client-visible signal of the post-connect teardown (the `Disconnected` event and `ConnectionState` leaving `Connected`), which is exactly what the diagnosis says surfaces to a caller. net10.0 and Discord.Net 3.20.2 are unchanged, and the version-bump path was evaluated and rejected with a specific rationale (confirmed).

Watch for: the settle window is a fixed 1.5s heuristic, not a signal that the second `VOICE_SERVER_UPDATE` handoff is done, so a handoff that lands after 1.5s still slips through as "stable" and dies mid-stream — the `DisconnectedToken` then cancels playback rather than reconnecting, so a late drop degrades to a failed/cut playback rather than a recovery (likely). A healthy cold join now always pays the full 1.5s before any audio plays (confirmed). `pcm.DisposeAsync()` in teardown is still the one unbounded step (confirmed, carried over from pass 1, not re-introduced here).

**Verdict**: APPROVED

## High-level view

The fix is built around the right observation. `ConnectAsync` returns the instant the first `Connected` event fires, and the teardown that kills the cold connect happens afterward on an internal task, invisible to the connect awaiter. `ConnectStableAsync` therefore stops trying to catch a throw and instead holds the returned client under observation for a settle window, trusting it only if it survives. This is a genuine redesign, not another wrap of the same call, and it matches the recorded diagnosis directly.

The settle window is the load-bearing heuristic. 1.5s is chosen to outlast the second `VOICE_SERVER_UPDATE` handoff that tears down the first cold connect. It is a time-based bet, not a confirmation that the handoff completed, so the fix is robust to a handoff that lands inside the window and fragile to one that lands after it. A late drop is caught by the `DisconnectedToken` as a cancelled playback, not a reconnect, which is a softer failure than the original bug but not a full recovery.

A single `handedOff` flag decides whether the `finally` tears the in-flight client down, so every non-handoff path disposes exactly once and ownership of the client and its disconnect token transfers to the caller via `Release`. The subscription-order detail (detach after `StopAsync`) is load-bearing and covered in the details.

The preserved pieces: `bufferMillis:40`, the ffmpeg argument string, the reader/writer split, the flush timeout, and the dispose-before-`StopAsync` ordering are intact. net10.0 and Discord.Net 3.20.2 stay, the Dockerfile DAVE setup and `EnableVoiceDaveEncryption` are outside the diff, and no text-only fallback was introduced.

<details>
<summary>Issues (3)</summary>

1. **Settle window is a fixed-time bet** — 1.5s is a heuristic for "the second VOICE_SERVER_UPDATE handoff has landed", not a confirmation of it. A handoff that completes after the window is treated as stable and then drops mid-stream, where the `DisconnectedToken` cancels playback rather than reconnecting. Acceptable given the observed timing (the teardown fires right after the handshake), but the window length is the one value that decides whether the fix holds on slower cold starts.
2. **Fixed latency on every healthy join** — Every cold join now waits the full 1.5s before the PCM stream is created, even when the connection was never going to drop. This is an intentional trade (correctness over latency) but it is a real added delay on the happy path.
3. **`pcm.DisposeAsync()` still unbounded in teardown** — Carried over from pass 1, not introduced here: disposal of the PCM stream is the only teardown step without a timeout. If disposal drains rather than cancels the queue, the "Disconnecting" hang could reappear through dispose. Out of scope for this pass but still open.

</details>

<details>
<summary>Details</summary>

### Why this fixes the post-connect cancellation and the retry did not

The diagnosis is that `SocketGuild.ConnectAudioAsync` completes its connect promise from the AudioClient's first `Connected` event and returns the `IAudioClient` immediately; the teardown comes later, when a second `VOICE_SERVER_UPDATE` makes Discord.Net restart the client, and `ConnectionManager.StartAsync` → `AcquireConnectionLock` → `StopAsync` → `Cancel()` cancels the in-flight connection promise on its own background task. That `TaskCanceledException` is raised at `ConnectionManager.<StartAsync>b__0`, after the awaited `ConnectAsync` has returned, so it is structurally impossible for a `try/catch` around `ConnectAsync` to see it — which is why the previous pass's `ConnectWithRetryAsync` never logged "attempt 2".

`ConnectStableAsync` does not depend on that throw at all. After `ConnectAsync` returns, it subscribes `OnDisconnected` to `IAudioClient.Disconnected` and waits on `Task.Delay(ConnectSettleWindow)` linked to both `ct` and a `disconnectedCts` that `OnDisconnected` cancels. The teardown's `Disconnected` event (and `ConnectionState` leaving `Connected`) is exactly the client-visible signal the diagnosis names, and it is what this code watches. The stability gate is explicit:

```csharp
if (client.ConnectionState == ConnectionState.Connected
    && !disconnectedCts.IsCancellationRequested)
{
    // hand back the live client
}
```

A connection torn down by the internal task fails this check (either `Disconnected` fired, cancelling `disconnectedCts`, or the state left `Connected`), so the attempt is treated as a failure and retried after a 500ms pause. This is the opposite of "wrap/retry `ConnectAsync` again": the retry is driven by an observed post-connect drop, not by an exception out of the connect call. The fix addresses the mandated failure mode.

### The settle window is a timing bet, not a handoff signal

The correctness of the whole approach rests on 1.5s being longer than the gap between `ConnectAsync` returning and the second `VOICE_SERVER_UPDATE` teardown. The user's log supports this — the handshake, "Connected", and `TaskCanceledException` all land in the same second — so the observed teardown is near-immediate and 1.5s covers it comfortably. The residual risk is a slower cold start where the handoff lands after the window: `ConnectStableAsync` would hand back a client that looks `Connected`, and the drop would then arrive mid-stream, where the linked `DisconnectedToken` cancels the write/copy chain. That path does not reconnect; it ends the playback early. So the failure mode is downgraded from "silent dead connection" to "playback cut short", but it is not a recovery. Nothing in the data suggests the handoff is that slow, and the window is tunable, but it is the one value the fix is sensitive to.

### Connection lifecycle and the handedOff flag

```
ConnectAsync -> client
  subscribe OnDisconnected -> disconnectedCts
  wait settle window (linked: ct + disconnectedCts)
    stable?  -> handedOff = true; return StableConnection(client, token, Release)
    unstable + attempts left -> log, delay, loop  (finally tears down)
    unstable + exhausted     -> throw             (finally tears down)
    ct cancelled             -> throw             (finally tears down)
```

The `finally` keys off `handedOff`: on every path except a successful return it detaches the handler, calls `SafeStopAsync` (which swallows a stop failure then disposes), and disposes the token source — so an in-flight client is torn down exactly once and never leaked or double-disposed. On the handoff path the `finally` leaves the client and CTS alone; ownership passes to the caller through `StableConnection.Release`.

`Release` is guarded by a `released` flag so it is idempotent, and `PlayWavAsync` invokes it after `StopAsync` in the outer `finally`. That ordering matters: the handler must stay subscribed while `StopAsync` performs the leave so the leave's own `Disconnected` fires into a live `disconnectedCts`, and only then is the handler detached and the CTS disposed — detaching before disposing closes the window where the self-inflicted `Disconnected` could fire into a disposed source. When playback runs to completion this means the `DisconnectedToken` is observed cancelled during normal teardown; playback is already finished by then, so nothing reads it.

### DisconnectedToken into the stream chain

The handed-back token is linked into `streamCts` alongside `playbackToken`, and `streamToken` now threads through the ffmpeg stdin write/flush and the `CopyToAsync` into the PCM stream. A drop after the settle window therefore cancels the stream work rather than letting it write into a dead transport — the mid-stream equivalent of the connect-time stability gate. The flush step correctly links off `streamToken` so a late drop also breaks the bounded flush wait.

### Preserved behavior

`bufferMillis: BufferMillis` with `BufferMillis = 40` is intact, with the preload-threshold comment carried forward. The ffmpeg argument string (`-i pipe:0 -ac 2 -f s16le -ar 48000 pipe:1`, 24kHz mono WAV to 48kHz stereo s16le) is unchanged, as is the single-writer/single-reader split and the stdin-close-for-EOF pattern. The playback timeout (2 min) and flush timeout (10s) tokens are preserved; teardown's ffmpeg wait/kill still uses the original `ct` rather than the playback token so cleanup runs after playback cancels. The dispose-PCM-before-`StopAsync`-before-dispose-client ordering is unchanged. No text-only fallback was added.

The git stat confirms `VoicePlayback.cs` is the only changed file (194 insertions, 32 deletions). `EnableVoiceDaveEncryption = true` in Worker.cs and the libdave fetch/symlink in the Dockerfile are outside the diff. csproj stays `net10.0` with `Discord.Net 3.20.2`; the test project is also `net10.0`.

### Version bump sanity check

The commit records that a bump was evaluated and rejected: 3.20.2 is the latest stable, and the one plausibly-related PR (#1580, "connection deadlock", merged 2020) concerns the gateway client's `OnConnectingAsync` rest-request cancellation, not the voice AudioClient restart, and is already in 3.20.2. That reasoning is consistent with the diagnosis — the bug is in the voice `ConnectionManager` restart path, which no released version after 3.20.2 is claimed to fix — so staying on 3.20.2 and fixing it in application code is the defensible call. Not independently re-verified against the Discord.Net changelog, but the recorded rationale is coherent.

### Build / warnings

Per the commit message, `dotnet build JainaProudmoore.slnx -c Release -warnaserror` succeeded with 0 warnings / 0 errors and `dotnet test` passed 14/14; `VoicePlayback` has no unit tests because it needs a live voice connection. Not re-run per instructions. The actual cold-start behavior (joins, stays connected through the DAVE handshake, plays TTS, leaves cleanly) can only be proven on the user's Unraid container, as the commit notes — the deliverable is not machine-verifiable here.

</details>

<details>
<summary>File map</summary>

- `JainaProudmoore/VoicePlayback.cs` — replaced `ConnectWithRetryAsync` with `ConnectStableAsync` + `StableConnection` record + `SafeStopAsync`; settle-window stability gate; `DisconnectedToken` linked into the playback stream; `handedOff`/`Release` lifecycle. Only file changed.

Full diff: `git show HEAD -- JainaProudmoore/VoicePlayback.cs`

</details>
