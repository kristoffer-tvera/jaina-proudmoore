# /askjaina voice, pass 5: are the DAVE decrypt spam and the connection teardown LINKED or INDEPENDENT?

## Short answer

**They share one trigger but are causally INDEPENDENT.** Both are consequences of the same event
— Discord.Net's `SocketGuild.FinishConnectAudio` running (once per `VOICE_SERVER_UPDATE`) — but
neither causes the other:

- The **DecryptionFailure spam** comes from `FinishConnectAudio` → `RepopulateAudioStreamsAsync`
  creating a per-speaker DAVE *input* (decrypt) stream for every other member in the channel,
  **unconditionally**. `selfDeaf` is never consulted on that path.
- The **connection teardown** (`TaskCanceledException at ConnectionManager.<StartAsync>b__0`) comes
  from `FinishConnectAudio` → `AudioClient.StartAsync` **restarting the already-connected client** on
  the *second* `VOICE_SERVER_UPDATE`, which cancels the in-flight connection promise.

Suppressing the receive/decrypt streams would **not** stop the teardown: the cancellation is raised
by the connection-lifecycle restart (`ConnectionManager.AcquireConnectionLock` → `StopAsync` →
`Cancel`), not by anything in the decrypt path. The decrypt failures are harmless log noise
(`DaveDecryptStream.WriteAsync` logs a warning and drops the packet; it never throws and never tears
the connection down). The real failure is the teardown, and that is what this pass fixes.

All source citations below are from the pinned build: Discord.Net tag `3.20.2`
(commit `be2c8de62d27e38c4e886c209eb1fd9fe4fca1f4`) with PR #3288 applied
(`patches/dave-pr-3288.patch`). File paths are repo-relative to that checkout.

---

## Evidence 1: where the decrypt spam actually comes from (and why selfDeaf can't stop it)

The exact warning string and logger name in the user's logs come from one place:

- `src/Discord.Net.WebSocket/Audio/Streams/DaveDecryptStream.cs`, `WriteAsync`: on a non-success
  decrypt it calls `_logger.WarningAsync($"Failed to decrypt audio packet for {_userId}: {result}")`
  then disposes the buffer and returns. The logger is named `"Dave decrypt stream {userId}"`
  (created in `AudioClient.CreateInputStreamAsync`). That matches the log lines verbatim. Critically,
  **it does not throw and does not signal the connection** — the packet is simply dropped. The
  libdave `(decryptor.cpp:157) ... no valid cryptor found` / `(decryptor.cpp:101) skipping silence`
  lines are native libdave output behind that same failed `Decrypt` call.

A `DaveDecryptStream` only exists inside the input-stream chain built by
`AudioClient.CreateInputStreamAsync` (`src/Discord.Net.WebSocket/Audio/AudioClient.cs`), which wraps
`OpusDecodeStream` in a `DaveDecryptStream` when `_dave is not null`. So the spam proves input
(receive-decode) streams are being created for the human.

Who creates them? **`SocketGuild`, unconditionally** — `selfDeaf` is never checked:

- `src/Discord.Net.WebSocket/Entities/Guilds/SocketGuild.cs`, `RepopulateAudioStreamsAsync`: clears
  all input streams, then `foreach` voice state in the bot's channel (except self) calls
  `await _audioClient.CreateInputStreamAsync(pair.Key)`.
- `AddOrUpdateVoiceStateAsync` in the same file: when another user's voice state changes into the
  bot's channel, it calls `CreateInputStreamAsync(model.UserId)`.

`RepopulateAudioStreamsAsync` is invoked from `SocketGuild.FinishConnectAudio` on **every**
`VOICE_SERVER_UPDATE` (the method that also restarts the client — see Evidence 2).

Independently, decryptors are also created regardless of `selfDeaf` in
`AudioClient.ProcessMessageAsync` (`AudioClient.cs`): `VoiceOpCode.Speaking` →
`_dave?.GetOrCreateDecryptor(data.UserId)` and `VoiceOpCode.ClientConnect` →
`_dave.GetOrCreateDecryptor(...)` for each connected user.

The inbound UDP path in `AudioClient.ProcessPacketAsync` only writes into a user's stream if one
exists in `_streams` (otherwise it logs `"Unknown User"` at Debug). Because
`RepopulateAudioStreamsAsync` populates `_streams` for the human, their packets reach the
`DaveDecryptStream`, which fails to decrypt (the bot is not a ratcheted recipient of that speaker's
media key under this build's DAVE handling) and emits the spam.

**Conclusion for problem 1:** there is **no supported knob** on this build to stop input-stream /
decryptor creation for a send-only bot. `selfDeaf` controls only the gateway voice-state flag; the
receive-decode streams are created by `SocketGuild` purely from channel membership, not from the
deaf flag or any `AudioClient`/`DiscordSocketConfig` option. The only ways to silence it would be to
patch the library (e.g. gate `RepopulateAudioStreamsAsync`/`AddOrUpdateVoiceStateAsync` on a
"receive disabled" flag, or make `DaveDecryptStream` log at Debug) or to raise the `Dave #N` /
`Dave decrypt stream` logger threshold above Warning. Since the spam is harmless and the task's hard
constraint is to preserve PR #3288 and the DAVE setup, this pass does **not** patch the library for
it; it documents the behavior in code comments and treats the spam as benign noise.

---

## Evidence 2: what actually cancels the connection, and when

The teardown is the real failure. The chain, from source:

1. On each `VOICE_SERVER_UPDATE`, `SocketGuild.FinishConnectAudio` runs
   (`src/Discord.Net.WebSocket/Entities/Guilds/SocketGuild.cs`). With an existing `_audioClient` it:
   - calls `RepopulateAudioStreamsAsync()` (the receive-stream rebuild — Evidence 1),
   - if `_audioClient.ConnectionState != ConnectionState.Disconnected`, awaits
     `_audioClient.WaitForDisconnectAsync(TimeSpan.FromSeconds(5))`,
   - `await Task.Delay(5ms)`,
   - `await _audioClient.StartAsync(url, ...)`.

2. `AudioClient.StartAsync` (`src/Discord.Net.WebSocket/Audio/AudioClient.cs`) calls
   `_connection.StartAsync()`.

3. `ConnectionManager.StartAsync` (`src/Discord.Net.WebSocket/ConnectionManager.cs`) begins with
   `await AcquireConnectionLock()`. `AcquireConnectionLock` loops `await StopAsync()` then tries the
   state lock. `StopAsync()` calls `Cancel()`.

4. `ConnectionManager.Cancel()` runs:
   ```
   _readyPromise?.TrySetCanceled();
   _connectionPromise?.TrySetCanceled();   // <-- cancels the in-flight connection promise
   _reconnectCancelToken?.Cancel();
   _connectionCancelToken?.Cancel();
   ```
   The already-running connection task is sitting at `await _connectionPromise.Task` inside the
   `StartAsync` lambda. `TrySetCanceled()` makes that await throw `TaskCanceledException`, caught by
   the lambda's generic `catch (Exception ex)` which logs it and calls `DisconnectAsync(...)`. The
   stack frame is exactly `ConnectionManager.<>c__DisplayClass29_0.<<StartAsync>b__0>d.MoveNext()` —
   the frame the user reported.

5. `DisconnectAsync` sets state Disconnecting → runs `_onDisconnecting` → Disconnected, and raises
   `_disconnectedEvent`. In `AudioClient`'s constructor that is wired to the public
   `IAudioClient.Disconnected` event (`_connection.Disconnected += (ex, _) =>
   _disconnectedEvent.InvokeAsync(ex)`).

So the `TaskCanceledException` → Disconnecting → Disconnected sequence is **self-inflicted by the
library restarting its own client** on the second `VOICE_SERVER_UPDATE`. This matches pass 2's
diagnosis (review2.md) and is confirmed here against the actual source.

### Why it does NOT abandon the connection

After `Cancel()`, `AcquireConnectionLock` keeps looping until the old task's `finally
{ _stateLock.Release(); }` runs; then it acquires the lock and `ConnectionManager.StartAsync` spins up
a **new** `_task` with a **new** `_reconnectCancelToken`, connecting to the handed-off voice server.
`SocketGuild.FinishConnectAudio` reuses the **same `_audioClient` instance** (it does not recreate
it), and `ConnectAudioAsync` returned that same instance to us. The Discord.Net docs confirm the
intent: the client "will sustain a connection to this channel until it is kicked, disconnected from
Discord, or told to disconnect"
([sending-voice guide](https://docs.discordnet.dev/guides/voice/sending-voice.html)). Content was
rephrased for compliance with licensing restrictions.

Net effect: the same `IAudioClient` object cycles `Connected → Disconnected → Connecting → Connected`
during the handoff, and ends up Connected again on the new server.

---

## Why ConnectStableAsync's 1.5s settle window did NOT catch this

Pass 2 used a fixed 1.5s settle window that fires **once**: connect, wait 1.5s, and if still
`Connected` hand the client to playback. The timing it bet on is wrong for this path:

- `FinishConnectAudio` gates the restart behind `WaitForDisconnectAsync(TimeSpan.FromSeconds(5))`.
  Reading `AudioClient.WaitForDisconnectAsync`: it completes only on a `_connection.Disconnected`
  event or a 5s `TimeoutException`. On a healthy just-connected client nothing is disconnecting, so
  it waits the **full 5 seconds**, then `Task.Delay(5ms)`, then `StartAsync` (the restart). So the
  cancellation/teardown lands roughly **~5 seconds after** the first "Connected".
- The 1.5s window elapses long before that. At 1.5s the client is genuinely `Connected`, so pass 2
  handed it back as "stable". The restart then dropped it mid-stream ~3.5s later, the
  `DisconnectedToken` cancelled the write/copy chain, and playback was cut — "joins, then leaves
  after ~1s" with no audio.

So it is not that the `Disconnected` event failed to fire; it fires, just **outside** the 1.5s
window. Pass 2's own "watch for" note flagged exactly this ("a handoff that lands after 1.5s still
slips through"). The field logs are that case.

A second, subtler problem: even when pass 2 *did* catch a drop inside the window, it called
`SafeStopAsync` (→ `StopAsync` → `Cancel`) to tear the client down and then re-`ConnectAsync`. That
**fights the library's own reconnect**: the library was already bringing the same client back up, and
tearing it down cancels that in-flight recovery.

---

## The fix (pass 5): ride the restart instead of racing or fighting it

`ConnectStableAsync` is reworked (only `JainaProudmoore/VoicePlayback.cs` changed) to match the real
lifecycle:

- Connect once, keep the **same** `IAudioClient` for the whole attempt.
- Require the connection to hold `ConnectionState.Connected` **continuously** for a quiet period
  (`StableQuietPeriod = 7s`, chosen to clear the 5s `WaitForDisconnectAsync` restart gate + the 5ms
  delay + reconnect time) before trusting it and creating the PCM stream.
- On a `Disconnected` **inside** the quiet period, treat it as the expected library restart: do
  **not** tear the client down. Re-arm the disconnect token source (`RearmDisconnect`) and, via
  `WaitForConnectedAsync`, wait (bounded by `ReconnectWait = 10s`) for the library's own reconnect
  loop to bring the same client back to `Connected`, then restart the quiet period.
- Bound the whole thing with `StableConnectDeadline = 30s` so a genuinely dead connection fails with
  a clear `InvalidOperationException` instead of hanging playback.
- The disconnect handler captures the `disconnectedCts` **variable** (not a snapshot), so after a
  `RearmDisconnect` swap the next `Disconnected` cancels the current source. On hand-off, ownership
  of the live client + token source transfers to the caller through `StableConnection.Release`
  (idempotent, detaches the handler then disposes); the `finally` only tears down on non-hand-off
  paths, so there is no leak or double-dispose.

The preserved behavior from earlier passes is untouched: `bufferMillis:40`, the ffmpeg argument
string and reader/writer split, the playback/flush timeouts, the `DisconnectedToken` linked into the
playback stream (so a *genuine* mid-stream drop after a stable connect still cancels cleanly), and
the dispose-PCM-before-`StopAsync`-before-dispose-client teardown ordering. `selfDeaf:true` is kept
(it is the correct voice-state for a send-only bot) with a corrected comment explaining it does not
gate the receive path on this build. PR #3288, the from-source build, `EnableVoiceDaveEncryption`,
libdave 1.2.2, and the Dockerfile are all outside this diff.

### What this does and does not promise

- It should make the connection **survive** the second-`VOICE_SERVER_UPDATE` restart that pass 2's
  window let through, because it explicitly waits out the ~5s restart gate and the reconnect rather
  than proceeding at 1.5s or tearing the client down. Audio is only started once the connection has
  proven durably `Connected`.
- It does **not** remove the DecryptionFailure spam. As established in Evidence 1, this build offers
  no supported way to stop input-stream/decryptor creation for a send-only bot; the spam is harmless
  and left as-is per the "preserve PR #3288 / DAVE setup" constraints. If the user wants it gone, the
  options are: (a) raise the `Dave #N` / `Dave decrypt stream {userId}` logger level above Warning in
  the app's log filter; (b) a library patch gating `RepopulateAudioStreamsAsync` /
  `AddOrUpdateVoiceStateAsync` behind a receive-disabled flag or dropping `DaveDecryptStream`'s
  warning to Debug; or (c) wait for an upstream Discord.Net change. None are required for audio to
  work.

---

## Locally verified vs. needs the user's live run

**Locally verified (on this Windows dev box):**
- `dotnet build JainaProudmoore/JainaProudmoore.csproj -c Release -warnaserror` → **0 warnings, 0
  errors** (warning-clean under warnings-as-errors).
- `dotnet test JainaProudmoore.slnx -c Release` → **14/14 passed**. (`VoicePlayback` has no unit
  tests; it needs a live voice connection, native audio libs, and a real process.)
- The source analysis above was read from the actual pinned Discord.Net 3.20.2 source
  (`ConnectionManager.cs`, `AudioClient.cs`, `DaveSessionManager.cs`, `SocketGuild.cs`,
  `DaveDecryptStream.cs`) at commit `be2c8de62d27e38c4e886c209eb1fd9fe4fca1f4`, plus the PR #3288
  delta, not inferred.

**Needs the user's live Unraid run (cannot be reproduced locally):**
- The receive-stream spam and the restart teardown only occur with a **second human speaking** in the
  channel on a **cold** `/askjaina` — there is no voice server, no `VOICE_SERVER_UPDATE` handoff, and
  no second participant in a local build. So the actual proof is: rebuild the Unraid image, run a cold
  `/askjaina` with another human present, and confirm the bot **joins, STAYS connected** (no
  `ConnectionManager` `TaskCanceledException` teardown ends the session), and **Jaina's TTS plays
  aloud**.
- Whether `StableQuietPeriod = 7s` fully clears the restart gate in the field is the one tunable the
  fix is sensitive to; if a handoff on a slow cold start lands later than 7s of continuous
  `Connected`, raise `StableQuietPeriod`/`StableConnectDeadline`. The 7s value is derived from the 5s
  `WaitForDisconnectAsync` gate read in the source, but the exact field timing can only be confirmed
  on the live run.
- The DecryptionFailure spam is expected to still appear in the logs (harmless); it is not a failure.

## Source files read (pinned commit be2c8de62d27e38c4e886c209eb1fd9fe4fca1f4)

- `src/Discord.Net.WebSocket/ConnectionManager.cs` — `StartAsync`, `ConnectAsync`, `Cancel`,
  `AcquireConnectionLock`, `DisconnectAsync`.
- `src/Discord.Net.WebSocket/Audio/AudioClient.cs` — constructor wiring, `StartAsync`,
  `OnConnectingAsync`, `ProcessMessageAsync` (Speaking/ClientConnect decryptor creation),
  `ProcessPacketAsync`, `CreateInputStreamAsync`, `RepopulateAudioStreamsAsync` caller,
  `WaitForDisconnectAsync`.
- `src/Discord.Net.WebSocket/Audio/DaveSessionManager.cs` — decryptor lifecycle, PR #3288 delta
  (`GetRecognizedUserIds`).
- `src/Discord.Net.WebSocket/Entities/Guilds/SocketGuild.cs` — `ConnectAudioAsync`,
  `FinishConnectAudio`, `RepopulateAudioStreamsAsync`, `AddOrUpdateVoiceStateAsync`.
- `src/Discord.Net.WebSocket/Audio/Streams/DaveDecryptStream.cs` — the exact DecryptionFailure log.
- `patches/dave-pr-3288.patch` — the two-file DAVE welcome/proposals delta.
