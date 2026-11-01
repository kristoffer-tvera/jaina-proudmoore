# Review — FinishConnectAudio same-session guard patch (commit 4dd783d)

Verdict: APPROVED. No blockers.

## Scope reviewed
- `JainaProudmoore/patches/dave-finishconnectaudio-guard.patch` (new)
- `JainaProudmoore/Dockerfile` (discordnet stage wiring)
- `JainaProudmoore/patches/.gitattributes`, `patches/README.md`, `Unraid.md`
- Against the authoritative plan `investigation-finishconnectaudio-fix.md` (Q2 plan, Q4 risk checklist)

## Verification method
- Fresh shallow checkout of dev @ `bf159d412bb113a52354f73d9def4e5d540ba7fd` (matched `git rev-parse HEAD`), fetched exactly as the Dockerfile does.
- Ran `git apply --check` + `git apply` for `dave-pr-3288.patch` then `dave-finishconnectaudio-guard.patch` in sequence; inspected the resulting source. Build/compile results read from the commit message (not re-run), per the step prompt.

## Risk-checklist findings (each verified by name)

1. **Guard only suppresses restart when Connected AND endpoint+token match.** CONFIRMED. Applied SocketGuild.cs:1924 — `sameSession = _audioClient.ConnectionState == ConnectionState.Connected && url == _audioClient.CurrentEndpoint && token == _audioClient.CurrentToken;` and the restart block is wrapped in `if (!sameSession)` (line 1929). All three conjuncts required.

2. **Genuine region handoff (changed endpoint/token) still restarts.** CONFIRMED. A different `url` or `token` fails the equality conjuncts, so `sameSession` is false and the `WaitForDisconnectAsync`/`Task.Delay`/`StartAsync` block runs.

3. **/yoink channel move still reconnects.** CONFIRMED. `AddOrUpdateVoiceStateAsync` (SocketGuild.cs:1724) calls `_audioClient.StopAsync(StopReason.Moved)` on the bot's own channel change, driving the client off `Connected` before the move's VOICE_SERVER_UPDATE arrives. With `ConnectionState != Connected`, `sameSession` is false and the restart fires.

4. **First-connect unaffected.** CONFIRMED. A freshly built client is `Disconnected` with null `CurrentEndpoint`/`CurrentToken` (AudioClient.cs:54/72/73, set only in StartAsync). `sameSession` is false; normal StartAsync runs.

5. **Network-drop resume path in OnDisconnectingAsync untouched.** CONFIRMED. The guard patch touches only AudioClient.cs (accessor-only addition, 6/0) and SocketGuild.FinishConnectAudio (22/10). Grep of the patch for `OnDisconnectingAsync|_resuming|FinishDisconnect` returns no matches.

6. **RepopulateAudioStreamsAsync still runs.** CONFIRMED. It is called unconditionally at the top of the `if (_audioClient != null)` block, before and outside the `if (!sameSession)` guard (SocketGuild.cs:1918).

7. **Both patches apply cleanly together against dev @ bf159d41 and touch disjoint hunks.** CONFIRMED by direct re-run: `dave-pr-3288.patch` check=0, apply=0; `dave-finishconnectaudio-guard.patch` check=0, apply=0, in sequence. `git apply --numstat` shows guard touches `AudioClient.cs` + `SocketGuild.cs`; pr3288 touches `DaveSessionManager.cs` + `tools/Discord.Net.Dave/Utils.cs` — disjoint files/hunks.

## Hard constraints
- No app-code changes: commit diff is limited to the investigation doc, Dockerfile, patches/.gitattributes, patches/README.md, the new patch file, and Unraid.md. VoicePlayback.cs / Worker.cs / csproj untouched.
- dev pin (`bf159d41…`) and `dave-pr-3288.patch` unchanged (not in the diff).
- New patch wired into the Dockerfile DAVE-PR-3288 discordnet fence with COPY + `git apply --check && git apply`, placed after the existing `dave-pr-3288.patch` apply line.
- Patch file is LF-only (CR=0, LF=63); `patches/.gitattributes` has `dave-finishconnectaudio-guard.patch text eol=lf`.
- Second patch documented in `patches/README.md` and both patches referenced in `Unraid.md`.

## Accessor / comparison correctness
- `CurrentEndpoint => _url`, `CurrentToken => _token`; `_url`/`_token` are assigned in StartAsync from the same `url`/`token` arguments FinishConnectAudio passes, and `_url` is the bare endpoint host (matching `data.Endpoint`), so the string compare is apples-to-apples per the plan.

## Recorded verification evidence (from commit message, not re-run)
- git apply --check for both patches = exit 0 (independently reconfirmed here).
- from-source `dotnet build src/Discord.Net.WebSocket -c Release` = 0 warnings/0 errors (net8/9/10).
- app `dotnet build JainaProudmoore.csproj -c Release` = 0 warnings/0 errors.
- Live cold-/askjaina proof noted as only confirmable on the Unraid docker build+run (out of scope here).

## Conclusion
The implementation matches the authoritative Q2 plan exactly, satisfies all Q4 risk-checklist scenarios, violates no hard constraint, and both patches apply cleanly together. APPROVED.
