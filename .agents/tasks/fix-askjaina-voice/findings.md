# /askjaina voice — third pass: the MLS Welcome reset loop is missing credential verification on the bot side, and Discord.Net 3.20.2 gives the application no way to supply it

**Outcome: B — no application-level or safe version/config fix exists on Discord.Net 3.20.2.**

The connect-stability work from passes 1 and 2 (`ConnectStableAsync`, `bufferMillis:40`, ffmpeg transcoding, the Dockerfile libdave setup) is correct and is preserved untouched. It fixed the *earlier* failure (the cold connect being torn down by the second `VOICE_SERVER_UPDATE`). By keeping the connection alive long enough to actually complete the DAVE handshake, it exposed a *second, deeper* failure that only reproduces once another real human is in the channel: libdave rejects the MLS Welcome because the bot cannot verify the other member's identity credential, resets the session, and loops forever with zero audio.

This document explains, with evidence from the decompiled Discord.Net 3.20.2 source and the libdave headers, why that loop is the correct behaviour of a *patched* library reacting to a *missing credential-verification input on the bot side*, why no application code in this repository can supply that input, and what the real options are.

---

## 1. The observed loop, mapped to source

The live Unraid cold `/askjaina` run (with a real human, user ID `162180934144229376`, in the channel) loops ~150 times through:

```
(session.cpp:270) Attempted to verify credential for unrecognized user ID: 162180934144229376
(session.cpp:572) MLS welcome lists unrecognized user ID
(session.cpp:458) Group received in MLS welcome is not valid
(session.cpp:82)  Resetting MLS session
(session.cpp:65)  Initializing MLS session ... group ID 926188034980970586
(session.cpp:629) Created MLS leaf node
(session.cpp:680) Creating a pending MLS group
(session.cpp:692) Created a pending MLS group
(session.cpp:654) Generated key package
```

This is a group the bot is being **welcomed into** (the human is already a member). The sequence `verify credential for unrecognized user ID → welcome lists unrecognized user ID → welcome is not valid → reset` is libdave refusing a Welcome whose roster contains a participant the client did not recognise, then starting over.

### 1.1 Where "recognised" comes from in Discord.Net 3.20.2

Decompiled `Discord.Audio.DaveSessionManager` (from `Discord.Net.WebSocket` 3.20.2, repo commit `be2c8de62d27e38c4e886c209eb1fd9fe4fca1f4`). The Welcome handler passes the **keys of an internal decryptor dictionary** to libdave as the set of recognised user IDs:

```csharp
private async ValueTask OnMLSWelcomeAsync(ushort transitionId, ReadOnlyMemory<byte> payload)
{
    DaveWelcomeResult welcomeResult = _session.ProcessWelcome(payload, _decryptors.Keys);
    // if ProcessWelcome returns null (rejected) -> SendMLSInvalidCommitWelcome + HandleDaveProtocolInit  == the reset loop
    ...
}
```

`ProcessWelcome(welcome, recognizedUserIds)` is the exact binding documented in `Discord.Net.Dave.xml`:

> "Processes a welcome message for this session." — parameters `welcome` and `recognizedUserIds` ("The snowflake identifiers of any recognized users"). *(Content rephrased for compliance with licensing restrictions.)*

So libdave is told "the users I recognise are `_decryptors.Keys`". If the human's ID is **not** in that set when the Welcome is processed, libdave sees the human in the Welcome roster, cannot match it against a recognised user, and — correctly, post‑patch — rejects the Welcome.

### 1.2 What populates `_decryptors` (the recognised set)

The dictionary is keyed by user ID and populated **only** from internal gateway event handling in `AudioClient.ProcessMessageAsync` / `DaveSessionManager`:

- `VoiceOpCode.ClientConnect` (opcode 11) — `ClientsConnect.UserIds` → `_dave.GetOrCreateDecryptor(id)` for each. Discord documents opcode 11 as *also* sent on initial connection for each existing user.
- `VoiceOpCode.Speaking` (opcode 5) — `_dave.GetOrCreateDecryptor(speakingEvent.UserId)`.
- `DaveSessionManager.GetOrCreateDecryptor` is the sole writer; `ClientDisconnect` (opcode 13) removes.

Confirmed by decompilation that **no other roster source exists**:
- `ReadyEvent` (opcode 2) carries only the bot's own `ssrc`, `ip`, `port`, `modes` — **no member roster**.
- `SessionDescriptionEvent` (opcode 4) carries `secret_key`, `mode`, `dave_protocol_version` — **no roster**.
- `ClientFlags` (18) and `ClientPlatform` (20), which Discord also sends "on initial connection for each existing user", are explicitly **no‑ops** (`case VoiceOpCode.ClientFlags: case VoiceOpCode.ClientPlatform: break;`) — they do **not** feed the decryptor set.

**Root cause:** in the welcomed-into-existing-group scenario, the human's ID is not present in `_decryptors` at the moment `OnMLSWelcomeAsync` runs `ProcessWelcome(payload, _decryptors.Keys)` — either because the `ClientConnect` carrying that ID has not been processed yet, or because the only initial-roster signals the bot does receive for an existing member (`ClientFlags`/`ClientPlatform`) are discarded. libdave therefore rejects every Welcome, resets, regenerates a key package, is re-welcomed, and loops. Zero audio is ever transmitted.

Note the self/external-sender identity plumbing is **present and working** — the earlier log shows `Unmarshalling MLS external sender`, `SetExternalSender`, the bot's own `Generated key package`, and proposal processing. The gap is specifically the recognition of **other** members at Welcome time, not the bot's own identity key pair (the `persisted_key_pair.h` path). This rules out "the bot has no identity credential" as the cause: it has one; it just cannot vouch for anyone else.

---

## 2. Why this is NOT a libdave bug — the CVE-2026-104480 tie-in

[CVE-2026-104480](https://www.sentinelone.com/vulnerability-database/cve-2026-104480/) (CVSS 9.4; [dev.to write-up by onaeiuspkz](https://dev.to/onaeiuspkz/cve-2026-104480-discords-libdave-accepts-an-mls-welcome-it-should-refuse-5h5b)): libdave **before 1.2.0** wrongly *accepted* an MLS Welcome whose roster contained an unrecognised participant, letting an unauthorised member into the E2EE session. The fix shipped in 1.2.0: such a Welcome is now *rejected*. *(Content rephrased for compliance with licensing restrictions.)*

This image runs libdave **1.2.2** (`LIBDAVE_VERSION=1.2.2` in the Dockerfile, `libdave-Linux-X64-boringssl.zip`). So:

> `Group received in MLS welcome is not valid / unrecognized user ID` is the **patched** library doing exactly what CVE-2026-104480 requires: refusing a Welcome it cannot verify.

The loop is a **symptom of the bot failing to populate the recognised-member set** before the Welcome is verified — not a library defect. This is the same first-generation DAVE credential/verification class of failure seen across ecosystems: [discord.js #11419](https://github.com/discordjs/discord.js/issues/11419) (`@discordjs/voice` 0.19.x reconnect loops / zero audio), and multiple discord.py / openclaw reports of DAVE decrypt/verify failures around the March 2026 enforcement.

### 2.1 Why downgrading libdave is NOT an acceptable fix

Dropping `LIBDAVE_VERSION` below 1.2.0 would stop the loop — because the pre-patch library would *accept* the unverifiable Welcome. That re-introduces CVE-2026-104480 (a 9.4 confidentiality break that admits unauthorised members into the encrypted session) and merely silences the symptom while making the bot insecure. This is explicitly rejected. **Keep `LIBDAVE_VERSION=1.2.2`.**

---

## 3. Why no application-level fix is possible in this repository

The entire MLS credential-recognition path is `internal` to Discord.Net. The bot's only handle on a voice connection is the public `Discord.Audio.IAudioClient`, whose complete surface is:

```
ConnectionState, Latency, UdpLatency
events: Connected, Disconnected, LatencyUpdated, UdpLatencyUpdated,
        StreamCreated, StreamDestroyed, SpeakingUpdated, ClientDisconnected
GetStreams(), StopAsync(), SetSpeakingAsync(bool)
CreateOpusStream / CreateDirectOpusStream / CreatePCMStream / CreateDirectPCMStream
```

There is **no** public API to:
- add a user ID to the recognised/decryptor set,
- influence the order in which `ClientConnect` vs `DaveMLSWelcome` are processed,
- pre-seed MLS group membership, or
- otherwise supply libdave the credential input it needs.

`DaveSessionManager`, `_decryptors`, `ProcessWelcome`, and the recognised-user set are all `internal`/private inside `Discord.Net.WebSocket`. No amount of code in `VoicePlayback.cs` (or anywhere in the app) can reach them. Therefore **no application-level fix can make the Welcome verify.** Writing one would only be able to *hide* the loop (e.g. by giving up after N resets and tearing down), which still produces **zero audio** and violates the hard "voice output required" requirement. That would be fabricating a non-working fix, which this task explicitly forbids.

---

## 4. Why a version bump cannot help either

`Discord.Net` 3.20.2 is the newest build **anywhere**:
- NuGet stable: `Discord.Net`, `Discord.Net.WebSocket`, `Discord.Net.Dave` latest = **3.20.2**.
- Official nightly BaGet feed (`baget.discordnet.dev`): newest `Discord.Net.WebSocket` = **3.20.2** (no `3.20.3-*`, no `3.21.*` prerelease exists).

So a csproj bump (keeping `net10.0`) has **no newer target to point at**. The fix for the Welcome-roster recognition must come from a future Discord.Net release that does not yet exist. (This supersedes pass 2's narrower "no version fixes the connect restart" note: the specific gap now identified — populating the recognised set before Welcome verification — is likewise unfixed in any released or nightly build.)

---

## 5. Concrete options (all require action outside this codebase)

1. **Wait for / upgrade to a future Discord.Net that fixes DAVE Welcome-roster recognition.** The fix belongs in `DaveSessionManager.OnMLSWelcomeAsync` / `AudioClient.ProcessMessageAsync`: ensure every existing member's ID is in `_decryptors` before `ProcessWelcome` runs — e.g. by also seeding decryptors from `ClientFlags`/`ClientPlatform` (opcodes 18/20, currently no-ops) which Discord sends for each existing user on initial connect, or by deferring/re-running Welcome verification until the `ClientConnect` roster has been applied. Track upstream `discord-net/Discord.Net` for a release/nightly after 3.20.2 whose notes mention DAVE send / MLS Welcome / credential recognition, then bump the csproj (keep `net10.0`). File an upstream issue with the `session.cpp` log and the `ProcessWelcome(payload, _decryptors.Keys)` finding if none exists.
   - If/when such a build appears on the nightly feed, a stop-gap is to add the `baget.discordnet.dev` source and pin that prerelease — acceptable only if the user accepts running a nightly.

2. **Switch the voice-send path to a different .NET DAVE binding** that implements full send-side credential verification. No drop-in equivalent is currently known to be more complete than Discord.Net.Dave 3.20.2; this is a larger re-architecture (replace the Discord.Net voice/audio layer, or bridge audio through a library/process that has a working DAVE sender). Out of scope for an in-loop code change and not something to adopt without the user's direction.

3. **Documented workaround — none that preserves E2EE voice.** The only "make it stop looping" levers (downgrade libdave below 1.2.0, or silence the loop in app code) either re-introduce CVE-2026-104480 or still produce zero audio. Neither satisfies the requirement. A true workaround would need Discord to allow a non‑E2EE voice path, which the March 2026 enforcement removes for non‑stage voice channels.

**Recommendation:** Option 1 — keep everything as-is (stability fix, `bufferMillis:40`, ffmpeg, `LIBDAVE_VERSION=1.2.2`), and gate the real fix on a Discord.Net release that populates the recognised-member set before MLS Welcome verification. Do not downgrade libdave. Do not ship app code that only masks the loop.

---

## 6. What is locally verifiable vs. what needs the live run

**Locally verified on this machine (no code change was made — outcome B):**
- `dotnet build JainaProudmoore\JainaProudmoore.csproj -c Release -warnaserror` → **succeeded, 0 warnings / 0 errors**.
- `dotnet test JainaProudmoore.slnx -c Release` → **14/14 passed**.
- Discord.Net 3.20.2 source behaviour above was read by decompiling the NuGet-cached assemblies (`ilspycmd`) and the `Discord.Net.Dave` XML docs; version availability was checked against NuGet and the official nightly feed.

**Only verifiable on the user's live Unraid cold `/askjaina` run with a second human present:**
- Whether any given Discord.Net build actually completes the MLS Welcome without the reset loop and plays audio aloud. The Welcome-roster rejection reproduces **only** when another real participant is in the channel (the roster must list a member other than the bot), so it cannot be exercised locally or in CI. Any future version bump must be validated by that live run before being considered fixed.
