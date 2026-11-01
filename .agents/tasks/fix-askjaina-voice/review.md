# Review — /askjaina MLS Welcome credential-verification failure (third pass)

**Verdict: APPROVED**

The implement step produced outcome (B): an honest, evidence-backed findings report
(`.agents/tasks/fix-askjaina-voice/findings.md`, commit `fd62662`) concluding that no safe
application-, version-, or config-level fix exists on Discord.Net 3.20.2. No production code was
changed. I independently verified the report's central claims against the decompiled Discord.Net
3.20.2 assemblies and the `Discord.Net.Dave` XML docs in the local NuGet cache.

## What the implement step delivered
- `git diff f19253c..HEAD` shows a single added file: `findings.md` (136 lines). Working tree clean.
- No change to `VoicePlayback.cs`, `JainaProudmoore.csproj`, or `Dockerfile`.

## Verification of the diagnosis (independently confirmed)

1. **`ProcessWelcome` is fed the local decryptor keys.** Decompiled
   `Discord.Audio.DaveSessionManager.OnMLSWelcomeAsync` (3.20.2) contains verbatim:
   `DaveWelcomeResult welcomeResult = _session.ProcessWelcome(payload, _decryptors.Keys);`
   The `Discord.Net.Dave.xml` doc confirms the binding
   `DaveSession.ProcessWelcome(ReadOnlyMemory<byte> welcome, ICollection<ulong> recognizedUserIds)`
   and the native `SessionProcessWelcome(..., recognizedUserIds, recognizedUserIdsLength, ...)`.

2. **The recognized set is populated only from internal gateway events.** In decompiled
   `AudioClient.ProcessMessageAsync`, `_dave.GetOrCreateDecryptor(...)` is called only from
   `VoiceOpCode.ClientConnect` (each `UserIds[i]`) and `VoiceOpCode.Speaking` (the speaking user),
   plus per-user decrypt-stream creation; `ClientDisconnect` removes. `GetOrCreateDecryptor`/
   `RemoveUser` are the only writers of `_decryptors`.

3. **The initial-roster signals for existing members are discarded.**
   `case VoiceOpCode.ClientFlags: case VoiceOpCode.ClientPlatform: break;` — literal no-ops.
   `Ready` carries only SSRC/ip/port/modes; `SessionDescription` carries only secret_key/mode/
   dave_protocol_version. Neither feeds the decryptor set. This confirms the report's root cause:
   in the welcomed-into-existing-group case the human's ID is absent from `_decryptors.Keys` when
   `ProcessWelcome` runs, so libdave 1.2.2 rejects the Welcome and resets, looping forever.

4. **No public hook exists.** The public `Discord.Audio.IAudioClient` surface (from
   `Discord.Net.Core` 3.20.2) is exactly as the report enumerates — connection state/latency,
   events, `GetStreams/StopAsync/SetSpeakingAsync`, and the four stream-creation methods. There is
   no API to add a recognized user, pre-seed MLS membership, or influence Welcome ordering.
   `DaveSessionManager`, `_decryptors`, and `ProcessWelcome` are internal. No app code can supply
   the missing credential input.

## Verdict-criteria check
- **CVE-2026-104480 consistency:** PASS. The report correctly frames the reset loop as libdave
  >= 1.2.0 (running 1.2.2) doing the right thing by refusing an unverifiable Welcome, and
  explicitly rejects downgrading below 1.2.0 as reintroducing the CVE.
- **Root cause = bot-side credential verification gap:** PASS. Attributed to the bot failing to
  populate the recognized-member set before Welcome verification (Discord.Net sending-side
  plumbing), explicitly NOT a libdave bug and NOT connect timing. It also correctly notes the
  bot's own identity/external-sender path is present and working, ruling out "no own credential."
- **No silence/suppress/retry workaround:** PASS. No such change was made; the report explicitly
  rejects log-masking and reset-giving-up as still producing zero audio.
- **Stability work preserved:** PASS. `ConnectStableAsync`, `bufferMillis:40`, ffmpeg transcoding,
  and the Dockerfile libdave setup (`LIBDAVE_VERSION=1.2.2`) are untouched. No command removed. No
  text-only audio fallback introduced.
- **Evidence-backed, not a cop-out:** PASS. Grounded in decompiled 3.20.2 source and Dave XML docs
  (independently reproduced above), plus the no-newer-version finding (3.20.2 is newest on NuGet /
  nightly feed). Options listed are concrete and correct.
- **Build evidence:** The commit records `dotnet build JainaProudmoore.csproj -c Release
  -warnaserror -> 0 warnings/0 errors` and `dotnet test -> 14/14 passed`. Per instructions I did
  not re-run the suites; since only a docs file was added, the build state is unchanged from
  origin/master.

## Findings
None. No blockers.
