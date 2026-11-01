# /askjaina voice — the FinishConnectAudio teardown fix (dev @ bf159d41)

READ-ONLY investigation. No code was changed, no patch files were written. All source citations are
from a fresh **shallow checkout of the pinned dev commit** `bf159d412bb113a52354f73d9def4e5d540ba7fd`
(verified via `git rev-parse HEAD`), fetched exactly as the Dockerfile's `discordnet` stage does. Paths
are repo-relative to that checkout. This report builds on — and does not re-derive — the prior root
cause in `investigation-connection-recovery.md` (traced against tag 3.20.2 / `be2c8de6`); its Q1 chain
is re-verified here against dev and found **byte-for-byte identical**.

---

## Summary answer (bottom line first)

1. **Yes — the abandonment is still present on dev @ bf159d41, unchanged.** On the second
   `VOICE_SERVER_UPDATE`, `SocketGuild.FinishConnectAudio` (SocketGuild.cs:1908) still unconditionally
   restarts the already-`Connected` client (`WaitForDisconnectAsync(5s)` → `StartAsync`), which drives
   `ConnectionManager.AcquireConnectionLock` → `Cancel()` → the old task's `DisconnectAsync(ex, false)`
   → `AudioClient.FinishDisconnect(ex, wontTryReconnect:true)` → `IsFinished = true` → the guild's
   `Disconnected` handler nulls+disposes `_audioClient`. No endpoint/token equality check exists; the
   restart fires on **every** server update while a client exists. There are **no dev-introduced
   differences** near this code versus the 3.20.2 findings.

2. **The minimal, safest fix is a guard in `FinishConnectAudio`.** Store the current endpoint/token on
   the `AudioClient` (they already exist as the private fields `_url`/`_token`, set in `StartAsync`;
   just expose them read-only as `internal`), and in `FinishConnectAudio` skip the destructive
   `WaitForDisconnectAsync`+`StartAsync` block when the client is **currently `Connected`** AND the
   incoming `url`/`token` equal the stored ones. A genuine region handoff (different endpoint/token) or
   a channel move (client already driven toward `Disconnected` via `StopReason.Moved`) does **not**
   satisfy the guard, so those still reconnect. Precise patch plan in Q2.

3. **No upstream PR fixes this — and the PR that the brief hoped might, #2873, is the one that
   *introduced* the teardown.** PR #2873 ("[Feature] Voice reconnection and resuming" by lepterion) is
   **already merged into dev**: its diff is the exact source now present at bf159d41 (the
   `WaitForDisconnectAsync`+`StartAsync` restart, `StopReason`, `FinishDisconnect`, `IsFinished`, the
   resume path). A reviewer on that very PR reported the identical `TaskCanceledException at
   ConnectionManager …StartAsync…b__0` and it merged anyway. #2247 is an old *issue* describing the
   related "Cannot start an already running client" symptom, not a merged fix. #3288 (already applied)
   is unrelated (DAVE welcome roster). So there is nothing to lift.

4. **Recommendation: hand-author a second patch** (`patches/dave-finishconnectaudio-guard.patch`)
   alongside `dave-pr-3288.patch`, behind the existing `DAVE-PR-3288` marker. It is low-friction (the
   build already `git apply`s patches to a from-source dev tree) and isolated. Lifting an upstream PR
   (path a) is impossible because none exists; filing upstream and waiting (path c) leaves `/askjaina`
   broken indefinitely. The guard is narrow and the risk is bounded (Q4).

---

## Q1. On a duplicate/benign second `VOICE_SERVER_UPDATE`, does dev still restart and abandon the client?

**Yes, unconditionally, and the chain is identical to the 3.20.2 investigation.**

### Dispatch (unchanged)
`DiscordSocketClient.EventHandling.cs:1589`, inside `case "VOICE_SERVER_UPDATE"`:

```csharp
var data = (payload as JToken).ToObject<VoiceServerUpdateEvent>(_serializer);
...
if (isCached)
    var _ = guild.FinishConnectAudio(data.Endpoint, data.Token).ConfigureAwait(false);
```

Fire-and-forget; the discarded `var _`. Discord pushes this second update on its own cadence (region
handoff, or here coinciding with DAVE/MLS epoch 1 settling after the human joins), so the ~7s timing is
server-driven, exactly as the prior report concluded.

### The restart branch (the exact quote requested)
`SocketGuild.FinishConnectAudio` (SocketGuild.cs:1908). The whole `if (_audioClient != null)` body:

```csharp
if (_audioClient != null)
{
    await RepopulateAudioStreamsAsync().ConfigureAwait(false);

    if (_audioClient.ConnectionState != ConnectionState.Disconnected)
    {
        try
        {
            await _audioClient.WaitForDisconnectAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await Discord.LogManager.WarningAsync("Failed to wait for disconnect audio client in time", null).ConfigureAwait(false);
        }
    }

    await Task.Delay(TimeSpan.FromMilliseconds(5)).ConfigureAwait(false);

    await _audioClient.StartAsync(url, Discord.CurrentUser.Id, voiceState.VoiceSessionId, token).ConfigureAwait(false);
}
```

There is **no condition on whether `url`/`token` changed**. A healthy `Connected` client takes the
`ConnectionState != Disconnected` branch, waits (or times out) for a disconnect that no one requested,
then `StartAsync` restarts it. `StartAsync` restarting an already-running client is what tears it down.

### Why the restart abandons the client (chain re-verified on dev)

- `AudioClient.StartAsync` (AudioClient.cs:119) sets `_url/_userId/_sessionId/_token` then calls
  `_connection.StartAsync()`.
- `ConnectionManager.StartAsync` (ConnectionManager.cs:59) guards
  `if (State != ConnectionState.Disconnected) throw "Cannot start an already running client."`, then
  `await AcquireConnectionLock()`.
- `AcquireConnectionLock` (ConnectionManager.cs:213) loops `await StopAsync()` →
  `Cancel()` (ConnectionManager.cs:181):

  ```csharp
  public void Cancel() {
      _readyPromise?.TrySetCanceled();
      _connectionPromise?.TrySetCanceled();   // cancels the OLD in-flight promise
      _reconnectCancelToken?.Cancel();         // kills the OLD task's reconnect loop
      _connectionCancelToken?.Cancel();
  }
  ```

- The OLD connection task (the `Task.Run` lambda at ConnectionManager.cs:68, awaiting
  `_connectionPromise.Task` at ~line 80) throws `TaskCanceledException`, caught at the loop's
  `catch (Exception ex)` (~line 90). Because `_reconnectCancelToken` was just cancelled, it takes the
  `else`: `await DisconnectAsync(ex, false)` — **`isReconnecting:false`**. The logged frame
  `Discord.ConnectionManager.<>c__DisplayClass29_0.<<StartAsync>b__0>d.MoveNext()` is this lambda —
  matching the live warn line exactly.
- `DisconnectAsync` → `_onDisconnecting(ex)` → `AudioClient.OnDisconnectingAsync` (AudioClient.cs:206).
  The `ex` is a bare `TaskCanceledException` (its `InnerException` is not a `WebSocketException`) and
  `_stopReason == Unknown`, so the resume/blacklist branch is **skipped** and it runs
  `await FinishDisconnect(ex, _stopReason != StopReason.Moved)` → **`wontTryReconnect:true`**
  (AudioClient.cs:241).
- `FinishDisconnect(ex, true)` (AudioClient.cs:252): `await _connection.StopAsync()` →
  `ClearInputStreamsAsync()` → **`IsFinished = true`** (AudioClient.cs:264).
- Back in `ConnectAudioAsync` (SocketGuild.cs:1803-1818) the per-client handler fires:

  ```csharp
  audioClient.Disconnected += async ex => {
      if (promise.Task.IsCompleted && audioClient.IsFinished) {   // both true
          try { audioClient.Dispose(); } catch { }
          _audioClient = null;                                    // guild drops the client
          ...
      }
  };
  ```

`_audioClient` is nulled+disposed. The same client is abandoned; nothing reconnects. **Definitively
identical to the prior investigation, on dev @ bf159d41.**

### Dev vs 3.20.2 diff near this code
None of substance. The restart block, `StopReason` enum (AudioClient.cs:865: `Unknown=0, Normal,
Disconnected, Moved`), `FinishDisconnect`, `IsFinished`, `WaitForDisconnectAsync`, the
`ConnectionManager` catch/`Cancel`/`AcquireConnectionLock` logic, and the `ConnectAudioAsync`
`Disconnected` handler are all present and structurally the same as the 3.20.2 findings. (The one dev
addition in this neighbourhood is DAVE wiring in `OnConnectingAsync` — `SetupLibDave()`,
`_dave?.MaxProtocolVersion` on `SendIdentityAsync` — which is orthogonal to the lifecycle.) The prior
report's root cause therefore stands unchanged on dev.

---

## Q2. Minimal, safest fix: the FinishConnectAudio endpoint/token guard (patch PLAN, not applied)

### The field/visibility obstacle, and how to overcome it minimally

The current endpoint/token are **already stored**, just not reachable from `FinishConnectAudio`:
`AudioClient` holds `private string _url, _sessionId, _token;` (AudioClient.cs:54), assigned in
`StartAsync` (AudioClient.cs:121-124). `SocketGuild._audioClient` is the concrete `AudioClient` type
(SocketGuild.cs:54), so `FinishConnectAudio` can read any `internal` member of it. The only obstacle is
that `_url`/`_token` are `private`. **Minimal fix: add two `internal` read-only accessors on
`AudioClient`** exposing the current url and token — no new state, no behavioural change, purely a
read surface. (`_url` here is the bare endpoint host, matching `data.Endpoint`; `wss://` is prepended
only at `OnConnectingAsync`, so a string compare against the incoming `url` argument is apples-to-apples.)

### The guard shape

In `FinishConnectAudio`, wrap the destructive restart so it runs **only** when the client is NOT a
healthy match for the incoming update. Treat it as a no-op (keep the live connection) when:

- `_audioClient.ConnectionState == ConnectionState.Connected`, **and**
- the incoming `url == _audioClient.CurrentEndpoint`, **and**
- the incoming `token == _audioClient.CurrentToken`.

`RepopulateAudioStreamsAsync()` should still run in the skip case (it only rebuilds receive streams for
present members and is harmless/idempotent — it is what the prior report labelled the benign decrypt
activity), OR be left as-is before the guard. Keeping it before the guard is simplest and preserves
current receive-stream behaviour for the newly-joined human.

### Precise patch plan (two files, analogous to `dave-pr-3288.patch`)

**File 1 — `src/Discord.Net.WebSocket/Audio/AudioClient.cs`:** add read-only accessors next to the
existing props (near AudioClient.cs:67, after `IsFinished`), returning the private fields:

```csharp
internal string CurrentEndpoint => _url;
internal string CurrentToken => _token;
```

(Context lines for the hunk: the field decl `private string _url, _sessionId, _token;` at line 54 and
the `internal bool IsFinished { get; private set; }` at line 67 anchor it cleanly.)

**File 2 — `src/Discord.Net.WebSocket/Entities/Guilds/SocketGuild.cs`:** inside
`FinishConnectAudio`'s `if (_audioClient != null)` block (SocketGuild.cs:1916), guard the restart:

```csharp
if (_audioClient != null)
{
    await RepopulateAudioStreamsAsync().ConfigureAwait(false);

    // A duplicate/benign VOICE_SERVER_UPDATE for the SAME endpoint+token on an
    // already-Connected client must NOT tear down the live connection. Only a genuine
    // endpoint/token change (region handoff) or a client already heading to Disconnected
    // (channel move via StopReason.Moved, network drop) should restart.
    var sameSession =
        _audioClient.ConnectionState == ConnectionState.Connected &&
        url == _audioClient.CurrentEndpoint &&
        token == _audioClient.CurrentToken;

    if (!sameSession)
    {
        if (_audioClient.ConnectionState != ConnectionState.Disconnected)
        {
            try
            {
                await _audioClient.WaitForDisconnectAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await Discord.LogManager.WarningAsync("Failed to wait for disconnect audio client in time", null).ConfigureAwait(false);
            }
        }

        await Task.Delay(TimeSpan.FromMilliseconds(5)).ConfigureAwait(false);

        await _audioClient.StartAsync(url, Discord.CurrentUser.Id, voiceState.VoiceSessionId, token).ConfigureAwait(false);
    }
}
```

The diff is a pure wrap: the existing three statements (the `if (ConnectionState != Disconnected)`
wait, the `Task.Delay(5ms)`, and the `StartAsync`) move one indent level inside `if (!sameSession)`;
only the `var sameSession = …` + `if (!sameSession)` lines are added. `RepopulateAudioStreamsAsync()`
stays before the guard, unchanged. No `catch`/`finally` in the method changes.

This mirrors `dave-pr-3288.patch`: a small, two-file unified diff that `git apply --check` can validate
against the pinned dev tree, applied in the `discordnet` build stage after the existing patch. Because
it only adds a guard and two getters, it will not conflict with `dave-pr-3288.patch` (different files /
different hunks: that patch touches `DaveSessionManager.cs` + `tools/.../Utils.cs`).

### Why this is the narrowest correct change
It does not touch `ConnectionManager` (the cancellation machinery is correct for its purpose), does not
alter `AudioClient` lifecycle semantics, and does not change `FinishDisconnect`/`OnDisconnectingAsync`.
It simply stops calling the restart path when there is nothing to restart. Everything else — real
handoffs, moves, drops — flows through the unchanged code.

---

## Q3. Upstream issue/PR landscape — does anything already fix this?

**No. The relevant PR is the cause, not the cure.**

- **PR #2873 — "[Feature] Voice reconnection and resuming" (lepterion) — MERGED into dev (it is the
  current bf159d41 source).** Its published diff adds exactly the code now present at bf159d41:
  `StopReason` enum, `_resuming`, `FinishDisconnect(ex, wontTryReconnect)`, `IsFinished`,
  `WaitForDisconnectAsync`, the `SendResume` path, the `ConnectAudioAsync` handler change from
  `if (!promise.Task.IsCompleted)` to `if (promise.Task.IsCompleted && audioClient.IsFinished)`, and
  the `FinishConnectAudio` `WaitForDisconnectAsync`+`StartAsync` restart block. This is verified by
  comparing the diff to the checked-out source: they match. **So #2873 introduced the restart-on-
  second-update behaviour that abandons the client — it does not fix it.** Tellingly, a reviewer
  comment on #2873 reports the **identical** symptom — `TaskCanceledException at
  Discord.ConnectionManager.<>c__DisplayClass29_0.<<StartAsync>b__0>d.MoveNext()` plus the matching
  `SocketGuild.ConnectAudioAsync` frames, with repro "move the bot to another channel, then call
  ConnectAsync" — and the PR merged regardless. There is no follow-up fix PR for that observation in
  what I could reach.
- **Issue #2247 — "Cannot start an already running client" (ObjectDisposedException /
  InvalidOperationException).** This is an *issue* report (v3.5.0-era), not a merged fix. It documents
  the *pre-#2873* form of the same collision: the second `VOICE_SERVER_UPDATE` reaching `StartAsync`
  while the client was still running threw `InvalidOperationException: Cannot start an already running
  client` straight out of `ConnectionManager.StartAsync`. #2873's `WaitForDisconnectAsync` was the
  attempt to avoid that throw — by first forcing the old client down — which is precisely how it turned
  a hard throw into a silent teardown. No diff on #2247 fixes the current behaviour.
- **PR #3288 — DAVE welcome roster + C-string null terminator (already applied).** Touches only
  `DaveSessionManager.cs` and `tools/Discord.Net.Dave/Utils.cs`. It is why the MLS welcome now
  succeeds; it does nothing to the connection lifecycle. Confirmed unrelated.
- **Issue #3154 — "Can't connect to voice channels" (endpoint port stripped / 4006 in some regions).**
  A different bug (endpoint parsing / region 4006), not the second-update teardown. Not applicable.
- General tracker/StackOverflow hits for "TaskCanceledException connecting to voice" describe the same
  surface symptom but offer no library fix; they are user reports, not merged diffs.

**Conclusion:** there is no approved/mergeable upstream PR to lift for this teardown. The #3288-style
"lift an upstream PR" strategy is unavailable here because the upstream state is "feature merged with
the defect baked in."

Sources (GitHub): PR [#2873](https://github.com/discord-net/Discord.Net/pull/2873) and its
[diff](https://patch-diff.githubusercontent.com/raw/discord-net/Discord.Net/pull/2873.diff); issue
[#2247](https://github.com/discord-net/Discord.Net/pull/2247); issue
[#3154](https://github.com/discord-net/Discord.Net/issues/3154); PR
[#3288](https://github.com/discord-net/Discord.Net/pull/3288). Content was rephrased for compliance with
licensing restrictions.

---

## Q4. Risk assessment — what a wrong guard could break, and what a correct fix must preserve

The guard keys on two things the live path already relies on: **current `ConnectionState`** and the
**stored endpoint/token**. The failure modes of getting it wrong:

1. **Legitimate region/server handoff (real second `VOICE_SERVER_UPDATE` with a NEW endpoint/token).**
   Discord moves the guild's voice to a different server; it sends a genuinely different `data.Endpoint`
   (and usually a new `token`). A guard that suppressed the restart here would strand the bot on a dead
   endpoint. **Preserved by design:** the guard only skips when `url == CurrentEndpoint && token ==
   CurrentToken`; a changed endpoint/token fails the match and restarts normally.

2. **Voice channel move via `/yoink` (and UI drag).** A move routes through
   `AddOrUpdateVoiceStateAsync` (SocketGuild.cs:1717): when the bot's own channel changes it sets
   `_audioClient.ChannelId` and calls `await _audioClient.StopAsync(StopReason.Moved)`. `StopReason.Moved`
   makes `OnDisconnectingAsync` call `FinishDisconnect(ex, /*wontTryReconnect:*/ false)` (because
   `_stopReason == Moved`), so `IsFinished` stays **false** and the client is NOT abandoned; the client
   is driven toward `Disconnected`, then the move's `VOICE_SERVER_UPDATE` arrives and
   `FinishConnectAudio` restarts it onto the new channel's server. **Preserved:** on a move the client
   is no longer `Connected` (it was explicitly `StopAsync(Moved)`'d), so `sameSession` is false and the
   restart still happens. Also, a move generally carries a new endpoint/token anyway. A reviewer MUST
   confirm the move path still reconnects after the guard (see checklist).

3. **Reconnect after network drop / resume.** A dropped socket surfaces as
   `_stopReason == Unknown && ex.InnerException is WebSocketException`, taking the **resume** branch in
   `OnDisconnectingAsync` (sets `_resuming = true`, no `FinishDisconnect`), independent of
   `FinishConnectAudio`. The guard does not touch this path. If a drop instead produces a fresh
   `VOICE_SERVER_UPDATE`, the client will not be `Connected` at that moment (it is reconnecting), so
   `sameSession` is false and the restart proceeds. **Preserved**, but a reviewer must confirm the guard
   never sees a `Connected`+same-session state during an in-progress resume (it should not, because a
   resume keeps the same endpoint only while the socket is down, i.e. not `Connected`).

4. **Initial connect (first `VOICE_SERVER_UPDATE`).** On first connect `_audioClient` was just created
   and never started, so `ConnectionState` is `Disconnected` and `CurrentEndpoint`/`CurrentToken` are
   null; `sameSession` is false and the normal `StartAsync` runs. **Preserved** — the guard only ever
   suppresses a restart of an already-`Connected` client.

5. **Rapid re-`/askjaina` / reconnect to same channel while connected.** `ConnectAudioAsync` with
   `disconnect:true` (the default) calls `DisconnectAudioInternalAsync()` first, nulling `_audioClient`,
   so the subsequent `VOICE_SERVER_UPDATE` builds a fresh client — guard inert. With `disconnect:false`
   and the same endpoint, suppressing the redundant restart is exactly the desired behaviour.

**What a correct fix MUST preserve (reviewer checklist):**
- First-connect still completes (guard inert when not `Connected`).
- Region handoff with changed endpoint/token still restarts (guard matches on equality only).
- `/yoink` / UI channel move still reconnects to the new channel (move went through
  `StopAsync(Moved)`, so not `Connected` at guard time; new endpoint anyway).
- Network-drop resume path is untouched (lives in `OnDisconnectingAsync`, not `FinishConnectAudio`).
- The benign receive-stream repopulation (`RepopulateAudioStreamsAsync`) still runs for a newly-joined
  human, so inbound decode is unaffected.
- No new throw: the guard replaces a teardown with a no-op; it never reaches
  `ConnectionManager.StartAsync`'s "already running" throw because it does not call `StartAsync` in the
  same-session case.
- Endpoint string comparison is against the bare host (`data.Endpoint` vs `_url`), not the `wss://…`
  form — the stored `_url` is the bare endpoint, matching the incoming argument.

---

## Q5. Bottom line — ONE recommended path

**(b) Hand-author a new `FinishConnectAudio` guard patch, applied as a second local patch alongside
`dave-pr-3288.patch`.** Use the Q2 plan.

Justification against the evidence:
- **(a) "lift an existing upstream PR" is not available.** The only PR in this area, #2873, is already
  merged into dev and is the *source* of the teardown (Q3). There is no approved fix PR to lift.
- **(c) "file upstream and wait" leaves `/askjaina` broken indefinitely.** The defect merged into dev
  despite a reviewer flagging the exact `TaskCanceledException` on #2873, so a timely upstream fix is
  not a safe bet, and the bot needs working voice now.
- **(b) is low-friction and isolated, exactly like #3288.** The image already builds Discord.Net from
  source in the `discordnet` stage and `git apply`s `dave-pr-3288.patch`; adding a second
  `git apply patches/dave-finishconnectaudio-guard.patch` line is a one-line Dockerfile change behind
  the existing `DAVE-PR-3288` marker. The two patches touch disjoint files/hunks, so they compose
  cleanly. Cleanup remains a single `grep DAVE-PR-3288` sweep.

Concrete next steps for the implementer (coding step, not this investigation):
1. Author `JainaProudmoore/patches/dave-finishconnectaudio-guard.patch` as the two-file unified diff in
   Q2 (accessors on `AudioClient.cs`; `sameSession` guard wrapping the restart in `SocketGuild.cs`).
   Generate it against a checkout of `bf159d41` so `git apply --check` passes on the pinned tree.
2. In `JainaProudmoore/Dockerfile`, in the `discordnet` stage, add after the existing
   `git apply … dave-pr-3288.patch` line:
   `COPY patches/dave-finishconnectaudio-guard.patch /tmp/…` + `git apply --check … && git apply …`,
   keeping it inside the `DAVE-PR-3288 BEGIN…END` fence.
3. Update `JainaProudmoore/patches/README.md` to document the second patch (what/why/cleanup), matching
   the existing DAVE-PR-3288 write-up style.
4. Rebuild and re-run a live cold `/askjaina` with a human present; expect the second
   `VOICE_SERVER_UPDATE` at ~7s to be a no-op (connection stays `Connected`, no
   `TaskCanceledException`, no `Disconnecting`), audio plays.

Note: this is a per-FEAT isolated change with no app-code (`VoicePlayback.cs`, csproj) edits required —
the fix lives entirely in the patched-from-source Discord.Net, consistent with the existing DAVE-PR-3288
wiring.

---

## Source files read (shallow checkout @ bf159d412bb113a52354f73d9def4e5d540ba7fd)

- `src/Discord.Net.WebSocket/Entities/Guilds/SocketGuild.cs` — `ConnectAudioAsync` (1774),
  `DisconnectAudioInternalAsync` (1867), `FinishConnectAudio` (1908), `RepopulateAudioStreamsAsync`
  (1952), `AddOrUpdateVoiceStateAsync` (1709, the `StopReason.Moved` path), `RemoveVoiceStateAsync`.
- `src/Discord.Net.WebSocket/Audio/AudioClient.cs` — field decls (`_url/_sessionId/_token` @54,
  `_stopReason` @58, `IsFinished` @67, `ConnectionState` @70), `StartAsync` (119), `StopAsync` (133/136),
  `OnConnectingAsync` (176, incl. DAVE wiring + resume branch), `OnDisconnectingAsync` (206),
  `FinishDisconnect` (252), `WaitForDisconnectAsync` (815), `StopReason` enum (865).
- `src/Discord.Net.WebSocket/ConnectionManager.cs` — `StartAsync` (59) + reconnect loop/catch,
  `StopAsync` (119), `DisconnectAsync` (165), `Cancel` (181), `Error`, `Reconnect`,
  `AcquireConnectionLock` (213).
- `src/Discord.Net.WebSocket/DiscordSocketClient.EventHandling.cs` — `VOICE_SERVER_UPDATE` dispatch and
  the `FinishConnectAudio(data.Endpoint, data.Token)` call site (1574-1589).
- `JainaProudmoore/patches/dave-pr-3288.patch` + `patches/README.md` + `Dockerfile` (`discordnet` stage)
  — for patch structure / wiring to mirror.
- Prior report `investigation-connection-recovery.md` — root-cause chain re-verified, not re-derived.

Discord.Net source and GitHub PR content were paraphrased with only short signatures/expressions quoted
for citation; rephrased for compliance with licensing restrictions.
