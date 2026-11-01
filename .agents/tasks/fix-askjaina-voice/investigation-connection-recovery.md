# /askjaina voice — connection-recovery root cause (post-pass-5)

READ-ONLY investigation. No code was changed. All citations are from a fresh checkout of
Discord.Net tag `3.20.2`, commit `be2c8de62d27e38c4e886c209eb1fd9fe4fca1f4` (verified via
`git rev-parse HEAD`), with PR #3288 (`patches/dave-pr-3288.patch`) reviewed for scope. Paths are
repo-relative to that checkout. The application file is
`f:\git\jaina-proudmoore\JainaProudmoore\VoicePlayback.cs`.

---

## Summary answer (bottom line first)

**A stable DAVE voice SEND connection is NOT achievable from application code on this exact
Discord.Net 3.20.2 + PR #3288 build.** This is an upstream library defect in the voice reconnect
lifecycle, not an app-tunable timing problem. The pass-5 "ride the restart" premise is **false against
the source**: after the second `VOICE_SERVER_UPDATE` restart cancels the connection, the library does
**not** reliably bring the *same* `AudioClient` back to `Connected`, and in the observed live case it
**tears the client down permanently** (`IsFinished = true`, `_audioClient = null`). There is no public
API surface that lets the app suppress the second restart, re-drive the connect, or obtain a handle to
any post-restart client. The specific upstream methods that must change are
`SocketGuild.FinishConnectAudio` and `Discord.ConnectionManager.StartAsync`/`AcquireConnectionLock`
(see Q5).

The ~7s (vs. pass-5's assumed ~5s) delay is explained below: the ~5s figure in `diagnosis5.md` is a
**partial** truth — `WaitForDisconnectAsync(5s)` is real, but what actually gates the teardown is *when
Discord sends the second `VOICE_SERVER_UPDATE`*, which is server-driven and independent of that 5s
timer. The 7s quiet period in `VoicePlayback.cs` cannot fix a connection that never comes back.

---

## Q1. After the restart cancels the connection, does the SAME AudioClient reconnect, or is it abandoned?

**It is abandoned / left disconnected in the observed path. The pass-5 assumption that it recovers is
wrong.** Here is the exact post-`Cancel()` trace.

### The restart entry point

On the second `VOICE_SERVER_UPDATE`, `DiscordSocketClient.EventHandling.cs:1589` fires
`guild.FinishConnectAudio(data.Endpoint, data.Token)` (fire-and-forget; note the discarded `var _`).
`SocketGuild.FinishConnectAudio`
(`src/Discord.Net.WebSocket/Entities/Guilds/SocketGuild.cs:1908`) runs, and because `_audioClient`
already exists it:

1. `RepopulateAudioStreamsAsync()` (line 1918) — rebuilds receive streams (the decrypt spam, Q4).
2. If not already `Disconnected`, `await _audioClient.WaitForDisconnectAsync(TimeSpan.FromSeconds(5))`
   (line 1924).
3. `await Task.Delay(TimeSpan.FromMilliseconds(5))` (line ~1933).
4. `await _audioClient.StartAsync(url, …)` (line ~1935) — **the restart**.

### What the restart does to the already-connected client

`AudioClient.StartAsync` (`src/Discord.Net.WebSocket/Audio/AudioClient.cs:116`) just calls
`_connection.StartAsync()`. `ConnectionManager.StartAsync`
(`src/Discord.Net.WebSocket/ConnectionManager.cs:61`) begins:

```csharp
if (State != ConnectionState.Disconnected)
    throw new InvalidOperationException("Cannot start an already running client.");
await AcquireConnectionLock().ConfigureAwait(false);
```

Since the client is `Connected`, it does **not** throw here only because `AcquireConnectionLock`
(`ConnectionManager.cs:208`) first forces a stop:

```csharp
private async Task AcquireConnectionLock() {
    while (true) {
        await StopAsync().ConfigureAwait(false);     // => Cancel()
        if (await _stateLock.WaitAsync(0).ConfigureAwait(false))
            break;
    }
}
```

`StopAsync()` → `Cancel()` (`ConnectionManager.cs:181`):

```csharp
public void Cancel() {
    _readyPromise?.TrySetCanceled();
    _connectionPromise?.TrySetCanceled();   // <-- the OLD in-flight promise
    _reconnectCancelToken?.Cancel();        // <-- cancels the OLD task's reconnect loop
    _connectionCancelToken?.Cancel();
}
```

### Why the OLD task exits for good (does not reconnect)

The OLD connection task (created by the *first* `StartAsync`, `ConnectionManager.cs:66-116`) is sitting
at `await _connectionPromise.Task` inside its loop body (line 80). `Cancel()` makes that await throw
`TaskCanceledException`, caught by the loop's `catch (Exception ex)` (line 92). The branch taken is
decided by `reconnectCancelToken.IsCancellationRequested`:

```csharp
catch (Exception ex) {
    Error(ex);
    if (!reconnectCancelToken.IsCancellationRequested) {   // FALSE — Cancel() just cancelled it
        await _logger.WarningAsync(ex).ConfigureAwait(false);
        await DisconnectAsync(ex, true).ConfigureAwait(false);   // not taken
    } else {
        await _logger.ErrorAsync(ex).ConfigureAwait(false);
        await DisconnectAsync(ex, false).ConfigureAwait(false);  // TAKEN: isReconnecting = false
    }
}
```

Because `Cancel()` set `_reconnectCancelToken.Cancel()`, the OLD task takes the `else` branch:
`DisconnectAsync(ex, isReconnecting: false)`. Then its `while (!reconnectCancelToken.IsCancellationRequested)`
condition (line 76) is now false, so the OLD task **falls out of the loop and ends**, running
`finally { _stateLock.Release(); }` (line 115). **The OLD task never reconnects** — its reconnect loop
is dead. This directly contradicts the pass-5 comment in `VoicePlayback.cs` ("the ConnectionManager
reconnect loop re-establishes the SAME IAudioClient"). The reconnect loop that pass-5 relied on is the
one that was just cancelled.

### The TaskCanceledException the user sees

The logged frame `Discord.ConnectionManager.<>c__DisplayClass29_0.<<StartAsync>b__0>d.MoveNext()` is
exactly this OLD task lambda (the `Task.Run` body at `ConnectionManager.cs:66`). `Error(ex)` +
`_logger.WarningAsync(ex)` produces the `warn: … (null) System.Threading.Tasks.TaskCanceledException`
line, then `DisconnectAsync` logs `Disconnecting` → `Disconnected` (`ConnectionManager.cs:155-169`).
This matches the live log at 09:40:46 precisely.

### Why recovery does NOT happen: `DisconnectAsync(ex, false)` finalises the client

`DisconnectAsync` (`ConnectionManager.cs:155`) sets state `Disconnecting`, calls
`_onDisconnecting(ex)` → `AudioClient.OnDisconnectingAsync`
(`AudioClient.cs:200`). With `_stopReason == Unknown` and `ex` being a `TaskCanceledException` (whose
`InnerException` is not a `WebSocketException`), the `_resuming`/blacklist branches are skipped and it
runs:

```csharp
await FinishDisconnect(ex, _stopReason != StopReason.Moved);   // wontTryReconnect = true
```

`FinishDisconnect(ex, wontTryReconnect: true)` (`AudioClient.cs:244`) then:

```csharp
if (wontTryReconnect) {
    await _connection.StopAsync().ConfigureAwait(false);   // Cancel() again — idempotent
    await ClearInputStreamsAsync().ConfigureAwait(false);
    IsFinished = true;                                      // <-- the client is now FINISHED
}
```

`IsFinished = true` is the terminal flag. Back in `SocketGuild.ConnectAudioAsync`
(`SocketGuild.cs:1800-1818`), the per-client `Disconnected` handler now fires its abandon path:

```csharp
audioClient.Disconnected += async ex => {
    if (promise.Task.IsCompleted && audioClient.IsFinished) {   // both true now
        try { audioClient.Dispose(); } catch { }
        _audioClient = null;                                    // <-- guild drops the client
        if (ex != null) await promise.TrySetExceptionAsync(ex);
        else            await promise.TrySetCanceledAsync();
    }
};
```

So the guild's `_audioClient` is **nulled and disposed**. There is no surviving client to come back to
`Connected`. **Definitively: in the observed path the same AudioClient is abandoned, not recovered.**

### The race that pass-5 half-saw (and why "sometimes it reconnects" is a trap)

There IS a code path where the restart's *new* task connects: if `FinishConnectAudio` reaches
`await _audioClient.StartAsync(...)` (step 4) **before** the OLD task has finalised with
`IsFinished = true`, then `ConnectionManager.StartAsync`'s guard
`if (State != ConnectionState.Disconnected) throw` would throw `InvalidOperationException` — unless the
OLD task has already driven `State` to `Disconnected`. The ordering here is a genuine race between the
OLD task's `DisconnectAsync` (running on the ConnectionManager background task) and
`FinishConnectAudio`'s awaited `WaitForDisconnectAsync(5s)` + `StartAsync`. `WaitForDisconnectAsync`
(`AudioClient.cs:~808`) completes on the OLD task's `Disconnected` event, so by the time
`FinishConnectAudio` calls `StartAsync`, the OLD task has usually already set `IsFinished = true` and
the guild handler has nulled `_audioClient`. Once `_audioClient` is null, `FinishConnectAudio`'s guard
`if (_audioClient != null)` means the `StartAsync` restart on line ~1935 never even runs — the method
exits having torn everything down. That is the observed "stays disconnected" outcome. Whether a restart
new-task ever wins the race is timing-dependent and not controllable from the app, which is exactly why
five successive app-level timing fixes have failed against the same cancellation.

**Condition for (non-)recovery:** recovery would require the restart's fresh `StartAsync`/reconnect
task to be created *and* survive, but the library's own teardown of the OLD task sets `IsFinished` and
nulls the guild's client first. The app cannot influence that ordering.

---

## Q2. Why ~7s after Connected, not ~5s? Is diagnosis5's 5s `WaitForDisconnectAsync` claim correct?

**Partly. The 5s timer exists, but it is NOT what sets the Connected→teardown delay.** The real driver
is when Discord's gateway delivers the *second* `VOICE_SERVER_UPDATE`.

- `diagnosis5.md` claimed: on a healthy just-connected client, `WaitForDisconnectAsync(5s)` waits the
  full 5s (nothing is disconnecting), so the teardown lands ~5s after the first Connected.
- **Against source this reasoning is inverted.** `FinishConnectAudio` (and therefore
  `WaitForDisconnectAsync`) only runs *when the second `VOICE_SERVER_UPDATE` arrives*
  (`EventHandling.cs:1574-1589`). Nothing in Discord.Net polls for or schedules that second update; it
  is pushed by Discord's voice gateway on its own cadence (region/server handoff, or here coinciding
  with the DAVE/MLS epoch settling for the newly-joined human). So the clock from "first Connected" to
  "restart begins" is **network/server-timed**, not the 5s gate.
- The 5s `WaitForDisconnectAsync` timer runs *inside* `FinishConnectAudio`, i.e. *after* the second
  update already arrived. On a healthy connected client it is the path that logs
  `"Failed to wait for disconnect audio client in time"` (the `catch (TimeoutException)` at
  `SocketGuild.cs:~1927`) before the 5ms delay and the `StartAsync` restart. It adds up to 5s *on top
  of* whenever the second update landed; it does not define the 5s-from-Connected figure.

So the live ~7s = (time for Discord to send the second `VOICE_SERVER_UPDATE` after the first Connected,
here ≈2s, correlating with the 09:40:39 Connected → 09:40:42 decrypt activity → restart) + (up to 5s of
`WaitForDisconnectAsync`) + 5ms. The teardown logging at 09:40:46 is consistent with the second update
arriving a couple seconds post-Connected and the 5s wait then timing out. **The delay is not a fixed
5s and is not app-tunable** — which is the core reason the 7s quiet period in `ConnectStableAsync`
cannot be a reliable gate: there is no fixed interval that separates "healthy" from "about to be
torn down," because the teardown is triggered by an external event, and once triggered the client is
abandoned regardless of how long the app waited.

`WaitForDisconnectAsync` source confirmation (`AudioClient.cs:~790-820`): it returns immediately if
already `Disconnected`, otherwise completes on the next `_connection.Disconnected` event or throws
`TimeoutException` after `timeout`. The 5s value at the call site is correct; diagnosis5's framing of
what the 5s *measures* is not.

---

## Q3. Is there ANY application-reachable way to prevent the restart, grab the post-restart client, or get a stable send connection?

**No.** Enumeration of the relevant surface:

Public (reachable from `JainaProudmoore`):
- `IVoiceChannel.ConnectAsync(selfDeaf, selfMute, external, disconnect)` → `SocketGuild.ConnectAudioAsync`.
  Returns the `IAudioClient` from the FIRST `Connected` event; no parameter influences the second
  `VOICE_SERVER_UPDATE` handling.
- `IAudioClient`: `ConnectionState`, `Connected`/`Disconnected` events, `StartAsync`?/`StopAsync`,
  `CreatePCMStream`/`CreateOpusStream`/`Create*DirectStream`, `GetStreams`, `SetSpeakingAsync`,
  latency props, `Dispose`. **Observation only** — no method re-drives or replaces the connection, and
  `Disconnected` gives you an `Exception`, not a new client handle.
- `DiscordSocketConfig`: no option governs voice reconnect, the second-`VOICE_SERVER_UPDATE` restart,
  or DAVE receive-stream creation. `UseInteractionSnowflakeDate`, gateway intents, etc. are irrelevant
  here. There is no "disable voice receive" / "disable voice auto-restart" flag.
- `IGuild`/`SocketGuild`: `AudioClient` getter exposes the *current* client, but after abandonment it
  is `null` (see Q1). There is no event that hands you the post-restart client because, per Q1, no
  post-restart client is produced on this path.

Internal / not reachable (would need a library patch):
- `SocketGuild.FinishConnectAudio` (`internal`) — the method that performs the restart. Not callable or
  overridable by the app.
- `SocketGuild.RepopulateAudioStreamsAsync`, `AddOrUpdateVoiceStateAsync`, `DisconnectAudioInternalAsync`
  — all `internal`.
- `AudioClient` (the concrete type), `AudioClient.StartAsync(url,…)`, `IsFinished`,
  `WaitForDisconnectAsync`, `StopReason` — all `internal`.
- `Discord.ConnectionManager` and every member (`StartAsync`, `AcquireConnectionLock`, `Cancel`,
  `Reconnect`, `_reconnectCancelToken`, `_connectionPromise`) — `internal`/`private`.

There is no seam. The app cannot (a) prevent the second restart, (b) stop `Cancel()` from killing the
in-flight promise, (c) prevent `FinishDisconnect` from setting `IsFinished`, or (d) obtain a handle to
any reconnected client — all of it is library-internal and fires on the ConnectionManager's own
background task after `ConnectAsync` has already returned. The five app-level passes have each tried to
work this from the public surface (`ConnectionState` polling + `Disconnected` observation), which is the
only surface available, and none of it can reach the abandonment decision.

---

## Q4. Does the benign receive-decode activity interact with the restart timing?

**No causal linkage. They share a trigger but are independent — confirming diagnosis5's conclusion, but
with a correction to the "both trace to FinishConnectAudio" framing.**

- **Decrypt spam source:** `SocketGuild.RepopulateAudioStreamsAsync` (`SocketGuild.cs:1952`) clears
  input streams, then for every voice-state member in the bot's channel except self calls
  `_audioClient.CreateInputStreamAsync(pair.Key)`. `CreateInputStreamAsync` (`AudioClient.cs:~430`)
  wraps `OpusDecodeStream` in a `DaveDecryptStream` when `_dave is not null`. Inbound packets reach it
  via `ProcessPacketAsync` (`AudioClient.cs:~560`) only if `_streams` has the user. On a decrypt
  failure, `DaveDecryptStream.WriteAsync` logs `Failed to decrypt audio packet … DecryptionFailure` and
  **drops the packet — it never throws and never signals the connection**. This is the 09:40:42 spam.
- **It does NOT delay `WaitForDisconnectAsync` or move the restart.** `RepopulateAudioStreamsAsync` runs
  *before* `WaitForDisconnectAsync` within `FinishConnectAudio`, and is a bounded set of stream
  constructions; it does not await any network round-trip tied to the decrypt failures. The decrypt
  failures happen asynchronously on the UDP receive path (`ProcessPacketAsync`), on a different code
  path from the connection lifecycle. Neither feeds the other.
- **DAVE welcome/epoch does not drive a reconnect.** The PR #3288 welcome
  (`DaveSessionManager.OnMLSWelcomeAsync`, now using `GetRecognizedUserIds()`) and
  `ExecuteProtocolTransitionAsync` → `RebuildInputStreamsForDaveAsync` (`DaveSessionManager.cs:246`)
  all run inside `AudioClient.ProcessMessageAsync` on the existing session. They rebuild *decrypt*
  streams; they do **not** call `ConnectionManager.StartAsync`, emit a `VOICE_SERVER_UPDATE`, or touch
  the reconnect tokens. So the successful MLS welcome at 09:40:39 is unrelated to the teardown at
  09:40:46 beyond coincident timing.
- **Correction to diagnosis5:** the second `VOICE_SERVER_UPDATE` is **not** emitted by
  `FinishConnectAudio` — it is received from Discord's gateway (`EventHandling.cs:1574`).
  `FinishConnectAudio` is the *handler* of each update, not the source of the second one. So the spam
  and the teardown are not "both downstream of FinishConnectAudio running"; rather, the *second update*
  triggers one `FinishConnectAudio` invocation that both repopulates streams (spam) and restarts the
  client (teardown). The independence conclusion stands; the trigger attribution is refined.

**Ruled out:** the human speaking / decrypt spam does not cause, delay, or shape the teardown. The
teardown would occur on the second `VOICE_SERVER_UPDATE` with an empty channel too; the human just adds
receive streams that then log decrypt noise.

---

## Q5. Bottom line: achievable from app code, or upstream fix required?

**Not achievable from application code on this build. It requires an upstream Discord.Net fix.**

### Why no app fix can work
The failure is a library-internal decision — `ConnectionManager.Cancel()` kills the in-flight
connection promise and `AudioClient.FinishDisconnect(…, wontTryReconnect:true)` marks the client
`IsFinished`, after which `SocketGuild.ConnectAudioAsync`'s handler nulls and disposes `_audioClient`.
All of this is `internal`/`private`, runs on the ConnectionManager background task, and completes
before any public signal the app can act on. The app's only lever is observing `ConnectionState` and
`Disconnected`, neither of which can prevent abandonment or produce a replacement client. The five
passes (retry-around-ConnectAsync → fixed 1.5s settle → … → 7s "ride the restart") all operate on this
same inadequate public surface and therefore all fail against the same `TaskCanceledException`. The
7s quiet period is a bet on a delay that is externally timed and, more fundamentally, on a recovery
that does not occur.

### The specific upstream methods that would need to change
1. **`SocketGuild.FinishConnectAudio`** (`SocketGuild.cs:1908`): the "existing `_audioClient`"
   branch restarts a *healthy* connection on every second `VOICE_SERVER_UPDATE`. It should distinguish
   a true server handoff (new endpoint/token differing from the current session) from a benign/duplicate
   update and avoid tearing down a live, correctly-connected client. As written it unconditionally
   `Cancel()`s and restarts, which is the proximate cause.
2. **`Discord.ConnectionManager.StartAsync` / `AcquireConnectionLock` / `Cancel`**
   (`ConnectionManager.cs:61, 208, 181`): the restart path cancels `_connectionPromise` and the
   reconnect token of the live task, and the error classification in the loop's `catch`
   (`DisconnectAsync(ex, isReconnecting:false)` when the reconnect token is cancelled) drives
   `FinishDisconnect(wontTryReconnect:true)` → `IsFinished`. A correct fix would either resume/transfer
   the session to the new endpoint without finalising the client, or treat a handoff-driven restart as
   `isReconnecting:true` so the client is not abandoned.
3. **`AudioClient.OnDisconnectingAsync` / `FinishDisconnect`** (`AudioClient.cs:200, 244`): the
   TaskCanceled-from-handoff case falls through to `FinishDisconnect(wontTryReconnect:true)`; a
   handoff should instead be treated like the `_resuming`/reconnect path so the same client reconnects
   to the handed-off server.

### Upstream issue/PR landscape (beyond #3288)
PR #3288 (already applied) fixes only the DAVE **welcome roster** (`ProcessWelcome`/`ProcessProposals`
now include `SelfUserId` via `GetRecognizedUserIds()`) and a C-string null-terminator in
`tools/Discord.Net.Dave/Utils.cs`. It is why the MLS welcome at 09:40:39 now **succeeds** — but it does
nothing for the connection-restart lifecycle, which is a separate subsystem (`ConnectionManager` /
`SocketGuild.FinishConnectAudio`). No part of the connection-recovery path is touched by #3288.

I did not find, in this read-only source review, any 3.20.2 change addressing the voice
second-`VOICE_SERVER_UPDATE` restart / DAVE send-connection stability. A targeted search of the
Discord.Net issue tracker for an open issue/PR on "voice connection restart on second
VOICE_SERVER_UPDATE" / "AudioClient abandoned after TaskCanceledException" is the recommended next step
before filing; based on the code this is a live defect in 3.20.2 as pinned. (I flagged this rather than
assert a specific issue number I could not verify from source alone.)

### Recommendation (no code written)
Pursue the fix **upstream, not in `VoicePlayback.cs`**. The minimal viable upstream change is in
`SocketGuild.FinishConnectAudio`: skip the destructive `WaitForDisconnectAsync`+`StartAsync` restart
when the incoming endpoint/token match the already-`Connected` session (i.e. a duplicate/benign
`VOICE_SERVER_UPDATE`), so a healthy DAVE connection is never torn down. If a genuine endpoint change
must be honoured, route it through a resume/transfer that preserves the client rather than
`Cancel()` → `IsFinished`. Since the app already builds Discord.Net from source with PR #3288 applied,
an additional local patch to `FinishConnectAudio` (guarded on endpoint/token equality) is the most
practical path and keeps the DAVE setup intact. Until that patch exists, `ConnectStableAsync` cannot
deliver a stable send connection by any timing value, because the connection it is waiting on has
already been finalised and the guild's handle nulled.

The honest conclusion: **this is an upstream Discord.Net 3.20.2 limitation.** The evidence is the
abandonment path traced in Q1 (`Cancel` → reconnect-token cancelled → `DisconnectAsync(…, false)` →
`FinishDisconnect(…, true)` → `IsFinished` → guild `_audioClient = null`), none of which the public API
can intercept.

---

## Source files read (commit be2c8de62d27e38c4e886c209eb1fd9fe4fca1f4)

- `src/Discord.Net.WebSocket/ConnectionManager.cs` — `StartAsync`, `ConnectAsync`, the reconnect loop,
  `StopAsync`, `Cancel`, `Reconnect`, `DisconnectAsync`, `AcquireConnectionLock`, `Error`,
  `CriticalError`.
- `src/Discord.Net.WebSocket/Audio/AudioClient.cs` — ctor event wiring, `StartAsync`, `StopAsync`,
  `OnConnectingAsync`, `OnDisconnectingAsync`, `FinishDisconnect`, `ProcessMessageAsync`,
  `ProcessPacketAsync`, `CreateInputStreamAsync`, `RebuildInputStreamsForDaveAsync`,
  `ClearInputStreamsAsync`, `WaitForDisconnectAsync`, `CreatePCMStream`.
- `src/Discord.Net.WebSocket/Entities/Guilds/SocketGuild.cs` — `ConnectAudioAsync`,
  `DisconnectAudioInternalAsync`, `FinishConnectAudio`, `RepopulateAudioStreamsAsync`,
  `AddOrUpdateVoiceStateAsync`, `RemoveVoiceStateAsync`.
- `src/Discord.Net.WebSocket/DiscordSocketClient.EventHandling.cs` — `VOICE_STATE_UPDATE` and
  `VOICE_SERVER_UPDATE` dispatch (the `FinishConnectAudio` call site).
- `src/Discord.Net.WebSocket/Audio/DaveSessionManager.cs` — `OnMLSWelcomeAsync` (+ PR #3288 delta),
  `HandleDaveProtocolInitAsync`, `HandlePrepareEpochAsync`, `ExecuteProtocolTransitionAsync`,
  `RebuildInputStreamsForDaveAsync` caller.
- `JainaProudmoore/patches/dave-pr-3288.patch` — scope confirmed (DAVE welcome roster + C-string
  terminator only; no connection-lifecycle changes).
- `JainaProudmoore/VoicePlayback.cs` — the pass-5 `ConnectStableAsync` loop under evaluation.

Content from the Discord.Net source was paraphrased and only short signatures/expressions quoted for
citation; rephrased for compliance with licensing restrictions.
