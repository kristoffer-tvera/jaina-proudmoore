# /askjaina voice, pass 5: ride the library restart instead of racing a fixed settle window

Pass 5 answers the central question — are the DAVE decrypt spam and the ConnectionManager teardown linked or independent — and reworks `ConnectStableAsync` in `VoicePlayback.cs` to survive the teardown. The diagnosis concludes the two symptoms are INDEPENDENT: both are downstream of the same trigger (`SocketGuild.FinishConnectAudio` on the second `VOICE_SERVER_UPDATE`), but neither causes the other. The spam is per-speaker DAVE input streams created unconditionally by `RepopulateAudioStreamsAsync` and harmlessly dropped by `DaveDecryptStream.WriteAsync`; the teardown is the library restarting the same `AudioClient`, cancelling the in-flight connection promise. The fix stops racing the restart with a 1.5s window (pass 2) and instead keeps the same client, waits out the restart, and only creates the PCM stream once the connection holds `Connected` continuously for 7s. The spam is left in place as documented benign noise. Only `VoicePlayback.cs` and `diagnosis5.md` changed; PR #3288, the from-source build, libdave 1.2.2, `EnableVoiceDaveEncryption`, `bufferMillis:40`, ffmpeg, `selfDeaf:true`, and voice output are all untouched.

Watch for: the new load-bearing source fact — `FinishConnectAudio` gating its restart behind `WaitForDisconnectAsync(5s)`, which justifies the 7s quiet period — could not be re-read from the workspace because the Discord.Net source exists only inside the Docker build stage, not checked out here (possible; the ConnectionManager cancellation chain itself was confirmed against source in pass 2 and is cited consistently). The 7s quiet period is the one field-sensitive tunable: if a slow cold-start handoff lands after 7s of continuous `Connected`, it still slips through (likely, and acknowledged in the diagnosis). A reconnect that exceeds `ReconnectWait` (10s) surfaces as a bare `OperationCanceledException` while the overall-deadline path surfaces a clear `InvalidOperationException` (confirmed, minor asymmetry).

**Verdict**: APPROVED

## High-level view

The central question is answered with the right structure and specific citations. The spam and the teardown share one trigger (`FinishConnectAudio`) but have separate causal chains: the spam comes from `RepopulateAudioStreamsAsync`/`AddOrUpdateVoiceStateAsync` creating decrypt streams per channel member regardless of `selfDeaf`, and it never throws; the teardown comes from the same method restarting the `AudioClient`, which cancels the connection promise through `ConnectionManager.Cancel`. Suppressing the receive path would not stop the teardown, so the fix correctly targets the teardown and leaves the spam alone. This clears the gate that forbids a logs-only fix: the change does not quiet the decrypt warnings at all, it changes the connection lifecycle.

The design shift is sound. Pass 2 bet that 1.5s of `Connected` meant stable; the diagnosis shows the restart is gated behind a 5s disconnect-wait, so the teardown lands ~5s after the first `Connected` — outside the window — and then kills playback mid-stream. Pass 5 stops treating a drop as a failure to retry and instead rides it: keep the same client, wait for the library's own reconnect loop to bring it back, and require 7s of unbroken `Connected` before trusting it. That matches the recorded lifecycle rather than fighting it.

A single `handedOff` flag decides whether the `finally` stops and disposes the client, so every non-hand-off exit disposes exactly once; the re-arm of the disconnect source introduces one narrow lost-drop window covered in the details and Issue 4.

The gated constraints all hold. The diff is two files; the patch, Dockerfile, libdave version, DAVE encryption flag, buffer size, ffmpeg invocation, and send-only `selfDeaf` are outside it or unchanged, and there is no text-only fallback. The honest no-fix portion (the spam) cites source evidence and lists concrete options, which satisfies the no-fix gate.

<details>
<summary>Issues (5)</summary>

1. **New source fact not independently re-verifiable here** — The `WaitForDisconnectAsync(5s)` restart gate in `FinishConnectAudio` is the basis for the 7s quiet period, but the Discord.Net source is only cloned inside the Docker `discordnet` stage and is not in the workspace, so it could not be re-read during review. The claim is specific and consistent with pass 2's confirmed `ConnectionManager.Cancel` chain; treat the 5s figure as cited-but-not-reconfirmed and validate it on the live run.
2. **7s quiet period is a field-sensitive tunable** — Derived from the 5s gate + 5ms delay + reconnect time, but it is still a time bet. A handoff that lands after 7s of continuous `Connected` on a slow cold start slips through as stable and then dies mid-stream. The diagnosis flags this and names the knob to raise (`StableQuietPeriod`/`StableConnectDeadline`); confirm on the live run.
3. **Inconsistent failure surfacing between the two bounds** — Exhausting `StableConnectDeadline` (30s) throws a descriptive `InvalidOperationException`, but exhausting `ReconnectWait` (10s) inside `WaitForConnectedAsync` throws a bare `OperationCanceledException` that propagates out uncaught. Both correctly tear the client down, but the reconnect-timeout path loses the diagnostic message. Consider mapping it to the same clear failure.
4. **Narrow lost-drop window during reconnect re-arm** — In the not-Connected branch, `WaitForConnectedAsync` returns once `Connected`, then `RearmDisconnect` swaps the token source; a `Disconnected` firing between those two steps cancels the old source that is about to be disposed, so that signal is dropped. The next loop iteration re-checks `ConnectionState`, so it is caught on the following pass rather than lost outright. Low impact; worth a comment.
5. **Spam remains (intended, benign)** — The DecryptionFailure/decrypt-silence log flood is unchanged by design, documented as harmless with source reasoning and three concrete suppression options (raise the logger threshold, patch the library behind a receive-disabled flag, or wait for upstream). Not a defect, but the logs will still be noisy after this pass.

</details>

<details>
<summary>Details</summary>

### The central question: independent, with a shared trigger

The gate requires the diagnosis to establish, with source citations, whether the spam and the teardown are linked or independent. `diagnosis5.md` lands on independent-with-shared-trigger.

The spam chain: `SocketGuild.FinishConnectAudio` → `RepopulateAudioStreamsAsync`, which clears input streams and then creates one per other voice-state member via `AudioClient.CreateInputStreamAsync`, wrapping `OpusDecodeStream` in a `DaveDecryptStream` when DAVE is active. `AddOrUpdateVoiceStateAsync` does the same when a member's state changes into the channel. None of this consults `selfDeaf` — that flag is a gateway voice-state bit, not a receive switch on this build. The inbound UDP path (`ProcessPacketAsync`) only writes into a user's stream if `_streams` has an entry, which `RepopulateAudioStreamsAsync` has just populated, so the human's packets reach a `DaveDecryptStream` that cannot decrypt them and logs `DecryptionFailure`. Critically, `DaveDecryptStream.WriteAsync` logs and drops; it does not throw and does not signal the connection. That is the stated reason the spam cannot by itself cause the teardown.

The teardown chain: the same `FinishConnectAudio`, on an existing `_audioClient`, awaits `WaitForDisconnectAsync(5s)`, delays 5ms, then calls `AudioClient.StartAsync` → `ConnectionManager.StartAsync` → `AcquireConnectionLock` → `StopAsync` → `Cancel()`, and `Cancel()` does `_connectionPromise?.TrySetCanceled()`. The in-flight connect await throws `TaskCanceledException` at `ConnectionManager.<>c__DisplayClass29_0.<<StartAsync>b__0>d.MoveNext()` — the exact frame the user reported — then `DisconnectAsync` runs Disconnecting → Disconnected. This is the same chain pass 2's review confirmed against source; pass 5 adds the `WaitForDisconnectAsync(5s)` gate as the reason the teardown lands ~5s out.

The conclusion that suppressing the receive path would not stop the teardown follows directly: the cancellation originates in the connection-lifecycle restart, which runs whether or not input streams exist. This is the correct answer to the gate's linked-vs-independent question, and it is why a logs-only fix would not have addressed the real failure — which is also why the author did not attempt one.

The one caveat for the gate's "source evidence" requirement: the Discord.Net checkout is created only inside the Dockerfile's `discordnet` build stage (clone of tag `3.20.2` at `be2c8de`, then `git apply` of the PR). It is not present in the workspace, so the specific new claims (`RepopulateAudioStreamsAsync` unconditional creation; `WaitForDisconnectAsync(5s)`) could not be re-read line-for-line during this review. The `ConnectionManager.Cancel`/`AcquireConnectionLock` chain was already read against source in pass 2 and is cited identically here, and the PR #3288 patch content matches what the diagnosis describes (`GetRecognizedUserIds` over `_decryptors.Keys`). The citations are specific (file + method) and internally consistent, which meets the "sound source-evidenced diagnosis" bar the task sets; the live run is where the 5s/7s timing gets its final confirmation.

### Why pass 2's window missed the teardown, and how pass 5 rides it

Pass 2 connected, waited a fixed 1.5s, and handed back the client if it was still `Connected`. The diagnosis explains why that failed in the field: `WaitForDisconnectAsync` returns only on a `Disconnected` event or a 5s timeout, so on a healthy just-connected client it waits the full 5s before the restart fires. The teardown therefore lands ~5s after the first `Connected`, well past the 1.5s window — so pass 2 handed back a client that was genuinely `Connected` at 1.5s, and the restart then dropped it mid-stream, cancelling the playback chain through `DisconnectedToken`. That is the "joins, leaves after ~1s, no audio" the user saw, and pass 2's own "watch for" note predicted exactly this slower-handoff case.

Pass 5 restructures around the real lifecycle. It connects once and keeps the same `IAudioClient` for the whole attempt. The loop requires `ConnectionState.Connected` to hold continuously for `StableQuietPeriod` (7s): it waits `Task.Delay(7s)` linked to `disconnectedCts`, and a drop inside that window cancels the delay, which is caught and treated as the expected restart — re-arm the token source and loop. When the client is not `Connected` at the top of the loop (mid-restart), `WaitForConnectedAsync` polls until the library's reconnect loop brings the same client back up. The whole thing is bounded by `StableConnectDeadline` (30s) so a dead connection fails loudly instead of hanging. Only an uninterrupted 7s of `Connected` reaches the hand-off.

This is the right move given the diagnosis that the restart does not abandon the connection — the ConnectionManager reconnect loop re-establishes the same client on the handed-off server. Tearing the client down and reconnecting (pass 2) fought that recovery; riding it cooperates with it. The 7s value is reasoned from the 5s gate rather than guessed, which is a real improvement over the 1.5s bet, though it remains a time-based heuristic sensitive to field timing (Issue 2).

### Teardown accounting and token re-arming

The lifecycle bookkeeping is the part most likely to leak or double-dispose, and it holds up:

```
ConnectAsync -> client ; subscribe OnDisconnected -> disconnectedCts
loop:
  deadline exhausted            -> throw InvalidOperationException   (finally tears down)
  not Connected                 -> WaitForConnectedAsync; RearmDisconnect; continue
  Connected, 7s uncancelled     -> re-check Connected -> handedOff=true; return StableConnection
  Connected, dropped in window  -> catch; RearmDisconnect; continue
  reconnect wait exhausted      -> OperationCanceledException         (finally tears down)
```

`OnDisconnected` closes over the `disconnectedCts` variable, not a snapshot, so after a `RearmDisconnect` swap the next `Disconnected` cancels the current source. On hand-off the code captures `liveCts = disconnectedCts` into the `Release` closure, so `Release` disposes whichever source is live at hand-off and detaches the handler; `released` guards idempotency. The `finally` keys off `handedOff` and tears the client down on every non-hand-off exit exactly once. `PlayWavAsync` still invokes `Release` after `StopAsync`, so the leave's own `Disconnected` fires into a live source before the handler is detached. The re-arm introduces one narrow window (Issue 4) where a drop between "became Connected" and the swap is dropped, but the subsequent `ConnectionState` re-check catches it on the next iteration, so it degrades to a slightly later observation rather than a missed teardown.

### Failure surfacing asymmetry

`WaitForConnectedAsync` bounds itself with `ReconnectWait` (10s) linked to the deadline token. If the reconnect never completes within 10s, `waitToken.ThrowIfCancellationRequested()` throws a bare `OperationCanceledException`, which propagates out of the loop uncaught and through the `finally` (client torn down, `handedOff` false). That is correct behavior, but it loses the descriptive message the deadline path produces (`"...never held stable for 7s within the 30s budget..."`). A caller logging the failure sees a clear reason for a deadline timeout and an opaque cancellation for a reconnect timeout. Mapping the reconnect-timeout to the same `InvalidOperationException` would make the two timeout failures read consistently. Minor, non-blocking.

### Preserved behavior and gated constraints

Everything the gate protects is intact. The diff is `VoicePlayback.cs` + `diagnosis5.md` only, so PR #3288 (`patches/dave-pr-3288.patch` unchanged, still the `GetRecognizedUserIds` welcome/proposals delta), the from-source build stage, `LIBDAVE_VERSION=1.2.2` (no downgrade, CVE-2026-104480 intact), and the Dockerfile as a whole are outside it. Inside `VoicePlayback.cs`: `EnableVoiceDaveEncryption` is not touched (it lives in Worker.cs), `BufferMillis = 40` and its preload-threshold rationale are unchanged, the ffmpeg argument string and single-writer/single-reader split are unchanged, the playback/flush timeouts and the `DisconnectedToken`-into-`streamCts` linkage are preserved so a genuine post-stable drop still cancels the stream, and the dispose-PCM-before-`StopAsync`-before-dispose-client ordering is intact. `selfDeaf:true` is kept with a corrected comment that no longer claims it suppresses receive — it now explains the receive path is created unconditionally by `SocketGuild`. No text-only fallback was introduced; voice output is still the only path.

### The spam: honest no-fix with options

The gate allows a no-fix conclusion only if it cites source evidence and lists concrete options. The diagnosis does both: the spam is attributed to `RepopulateAudioStreamsAsync`/`AddOrUpdateVoiceStateAsync` creating decrypt streams regardless of `selfDeaf`, shown harmless because `DaveDecryptStream.WriteAsync` logs-and-drops, and left in place with three options (raise the `Dave #N`/`Dave decrypt stream {userId}` logger level above Warning; patch the library to gate receive-stream creation or drop the warning to Debug; or wait for an upstream change). None weaken PR #3288 or the DAVE setup, so leaving the spam is consistent with the constraints. This is an acceptable no-fix under the gate.

### Verification record (not re-run)

Per the commit message and `diagnosis5.md`: `dotnet build JainaProudmoore/JainaProudmoore.csproj -c Release -warnaserror` reported 0 warnings / 0 errors, and `dotnet test JainaProudmoore.slnx -c Release` passed 14/14 (`VoicePlayback` has no unit tests — it needs a live voice connection, native audio, and a real process). Not re-run per instructions. The live receive-stream + teardown behavior reproduces only with a second human speaking on a cold `/askjaina` and cannot be exercised locally, so the bar is a clean local build plus a sound source-evidenced diagnosis, which is met. The live proof remains: rebuild the Unraid image, run a cold `/askjaina` with another human present, and confirm the bot joins, stays connected (no `ConnectionManager` `TaskCanceledException` teardown), and plays Jaina's TTS; the spam is expected to persist and is harmless.

</details>

<details>
<summary>File map</summary>

- `JainaProudmoore/VoicePlayback.cs` — reworked `ConnectStableAsync` from a fixed 1.5s settle window + up-to-4 reconnect attempts to a single-client "ride the restart" loop: 7s continuous-`Connected` quiet period, `WaitForConnectedAsync` poll for the library's own reconnect, `RearmDisconnect` per ride-out, 30s overall deadline; corrected `selfDeaf` comment; preserved buffer/ffmpeg/timeout/teardown behavior. Only production file changed.
- `.agents/tasks/fix-askjaina-voice/diagnosis5.md` — the linked-vs-independent analysis with source citations and the spam no-fix rationale.

Full diff: `git show ce1e58e`

</details>
