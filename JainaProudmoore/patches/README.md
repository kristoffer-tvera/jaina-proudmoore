<!-- DAVE-PR-3288 -->
# DAVE-PR-3288: temporary from-source Discord.Net build

## What this is

`/askjaina` voice fails on Discord.Net **3.20.2** because its DAVE/MLS integration
rejects the MLS Welcome: it passes the keys of an internal decryptor dictionary as
the set of recognized user IDs (which never includes the bot itself), and
`Utils.ToCString` writes snowflake IDs to native memory **without a null
terminator**, so libdave reads past the end as a C string. The result is the
reset loop with zero audio seen in the live logs. Full analysis:
`../../.agents/tasks/fix-askjaina-voice/findings.md`.

The `/askjaina` voice path needs **two** batches of upstream work that no Discord.Net
NuGet release ships yet:

1. A batch of DAVE voice fixes (RTP padding [#3263], multi-user voice fixes, etc.)
   that are **merged into the `dev` branch but absent from the 3.20.2 release**. These
   address the live symptoms where the bot joins, completes the MLS welcome, but the
   connection is torn down ~7s in and/or audio is silent with 2+ users present.
2. The approved-but-still-open PR **#3288** (the welcome-roster / null-terminator fix),
   which is **not merged into dev** either.

So this image builds Discord.Net **from source at a pinned `dev` commit** (to pick up
batch 1) with PR #3288's 9-line, two-file delta applied on top (batch 2), packs the
patched components as `3.20.2` packages into a local NuGet feed, and lets the app
consume them without changing the app's `PackageReference`.

- **PR:** https://github.com/discord-net/Discord.Net/pull/3288 (approved by
  maintainer quinchs) — **STILL OPEN / not merged into `dev`**, so the patch below is
  still required.
- **Pinned dev commit:** `bf159d412bb113a52354f73d9def4e5d540ba7fd` (`dev` branch,
  dated 2026-10-26). We pin the **exact commit**, not the moving `dev` branch.
- **Patch:** [`dave-pr-3288.patch`](./dave-pr-3288.patch) — a `git apply`-able
  unified diff that applies **cleanly against the pinned dev commit** (verified with
  `git apply --check`), touching only
  `src/Discord.Net.WebSocket/Audio/DaveSessionManager.cs` (recognize every roster
  member of the welcome, including ourselves) and
  `tools/Discord.Net.Dave/Utils.cs` (reserve a byte for and write the C-string
  null terminator). dev still has `ProcessWelcome`/`ProcessProposals` passing only
  `_decryptors.Keys` and `ToCString` without a null terminator, so both halves of the
  patch are still needed.

We build the **pinned dev commit plus the PR's delta** — we do **not** vendor the
Discord.Net source into this repo and do **not** track the moving `dev` branch (we
check out the exact SHA above).

## Second patch: the FinishConnectAudio same-session guard

Even with the dev DAVE fixes and PR #3288 applied, `/askjaina` still joined, completed
the MLS welcome, then dropped at ~7s with
`TaskCanceledException … ConnectionManager …StartAsync…b__0`. The cause is a defect
**introduced by the already-merged** [PR #2873](https://github.com/discord-net/Discord.Net/pull/2873)
("[Feature] Voice reconnection and resuming"): on the second `VOICE_SERVER_UPDATE`
Discord sends shortly after connect, `SocketGuild.FinishConnectAudio` **unconditionally**
restarts the already-`Connected` `AudioClient` (`WaitForDisconnectAsync` then
`StartAsync`). Restarting a running client drives `ConnectionManager.AcquireConnectionLock`
→ `Cancel()`, which cancels the in-flight connection, runs `FinishDisconnect(wontTryReconnect:true)`,
sets `IsFinished`, and the guild's `Disconnected` handler nulls+disposes `_audioClient`.
No audio plays. There is **no upstream fix to lift**: #2873 is the cause, not the cure
(a reviewer flagged the identical `TaskCanceledException` on that PR and it merged anyway),
and issue #2247 only documents the pre-#2873 form of the same collision. Full analysis:
`../../.agents/tasks/fix-askjaina-voice/investigation-finishconnectaudio-fix.md`.

- **Patch:** [`dave-finishconnectaudio-guard.patch`](./dave-finishconnectaudio-guard.patch)
  — a `git apply`-able unified diff that applies **cleanly against the pinned dev commit**
  (verified with `git apply --check`, on top of `dave-pr-3288.patch`), touching only
  `src/Discord.Net.WebSocket/Audio/AudioClient.cs` (two `internal` read-only accessors,
  `CurrentEndpoint`/`CurrentToken`, exposing the already-stored private `_url`/`_token`
  fields — no new state, no behaviour change) and
  `src/Discord.Net.WebSocket/Entities/Guilds/SocketGuild.cs` (wrap the restart block in
  `if (!sameSession)`, where `sameSession` is "client is `Connected` AND the incoming
  `url`/`token` equal the stored ones").
- **Why it is safe:** the guard only suppresses the restart for a **benign duplicate**
  update on a live connection. A genuine region handoff (different endpoint/token), a
  channel move (`StopReason.Moved` drives the client off `Connected` first), a network-drop
  resume (lives in `OnDisconnectingAsync`, untouched), and first-connect
  (`Disconnected`/null fields) all fail the `sameSession` test and still restart normally.
  `RepopulateAudioStreamsAsync()` still runs in every case, so inbound decode for a
  newly-joined human is unaffected.
- **Disjoint from `dave-pr-3288.patch`:** different files and hunks (that patch touches
  `DaveSessionManager.cs` + `tools/Discord.Net.Dave/Utils.cs`), so the two compose cleanly
  when applied in sequence in the `discordnet` stage.

## How it is wired (the isolation)

Everything is behind the greppable marker `DAVE-PR-3288`:

1. **`patches/dave-pr-3288.patch`** and
   **`patches/dave-finishconnectaudio-guard.patch`** — the two standalone, auditable
   deltas (the PR #3288 DAVE welcome-roster/null-terminator fix, and the
   FinishConnectAudio same-session guard).
2. **`../Dockerfile`** — a dedicated `AS discordnet` build stage (fenced by
   `DAVE-PR-3288 BEGIN`/`END`) clones the `dev` branch, checks out the pinned commit
   `bf159d41…`, `git apply`s **both** patches in sequence (`dave-pr-3288.patch` then
   `dave-finishconnectaudio-guard.patch`), and `dotnet pack`s the patched
   `Discord.Net.*` components at version `3.20.2` into a local feed (dev's
   `VersionPrefix` is still `3.20.2`, and `IsTagBuild=true` drops the `-dev`
   `VersionSuffix`, so the packages are exactly `3.20.2`). The `build` stage copies
   that feed to `./local-packages` and copies `patches/NuGet.config` to the context
   root before `dotnet restore`.
3. **`patches/NuGet.config`** — prepends the `./local-packages` feed ahead of
   nuget.org. It is only applied inside the Docker build (it is **not** at the repo
   root, so the stock local/dev `dotnet build` ignores it).

The app's `JainaProudmoore.csproj` is **unchanged**: it still declares
`<PackageReference Include="Discord.Net" Version="3.20.2" />` (the dev build packs
at `3.20.2`, so no app-side version bump is needed). The `Discord.Net`
metapackage is a dependency-only shell with no assemblies; its shell still comes
from nuget.org, while its component dependencies
(`Discord.Net.WebSocket`, `Discord.Net.Dave`, `Discord.Net.Core`,
`Discord.Net.Rest`, `Discord.Net.Commands`, `Discord.Net.Webhook`,
`Discord.Net.Interactions`) resolve from the local patched feed because the local
source is preferred for the same version. The patched DAVE fix therefore lands in
the runtime image without any app-code or csproj change.

## Why `dotnet pack` to a local feed (not a ProjectReference / DLL swap)

Packing the patched libraries as `3.20.2` packages keeps the app csproj's
`PackageReference` untouched, so cleanup is purely "remove the local feed + build
stage + patch" with zero app-side diff. ProjectReference/DLL wiring would have
required marked edits inside the app project; the pack-to-feed route avoids them.

## Cleanup (do this once Discord.Net ships the fix upstream)

When a Discord.Net NuGet release (or a nightly you accept) contains **all** of the dev
DAVE voice fixes (batch 1), PR #3288 (batch 2), **and** a fix for the FinishConnectAudio
same-session teardown (the `dave-finishconnectaudio-guard.patch` concern):

1. `grep -r DAVE-PR-3288` from the repo root to find every touched spot.
2. In `../Dockerfile`, delete the whole `DAVE-PR-3288 BEGIN … END` `discordnet`
   stage and the three `DAVE-PR-3288`-marked lines in the `build` stage
   (the `COPY --from=discordnet …`, the `COPY patches/NuGet.config …`).
3. Delete this `patches/` directory (`dave-pr-3288.patch`,
   `dave-finishconnectaudio-guard.patch`, `NuGet.config`, `README.md`).
4. Set `<PackageReference Include="Discord.Net" Version="…" />` in
   `../JainaProudmoore.csproj` to the release that contains the fixes (it is
   currently `3.20.2` and was not bumped for this dev build, since dev packs at
   `3.20.2`).
5. Rebuild on Unraid and re-run the live `/askjaina` test.

That restores stock nuget.org resolution with no other change.
