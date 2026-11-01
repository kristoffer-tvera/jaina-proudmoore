# Review — Build Discord.Net from pinned dev commit for DAVE voice fixes

Commit under review: `b5af311` — "fix: build Discord.Net from pinned dev commit for DAVE voice fixes"
Files changed: `JainaProudmoore/Dockerfile`, `JainaProudmoore/patches/README.md`, `Unraid.md` (3 files, +74/-39).

## Verdict: APPROVED

No blockers found. The change meets the approved requirements and introduces no reachable correctness, security, or data failure.

## Evidence by requirement

### 1. Dockerfile discordnet stage — dev clone, pinned commit, guard, fencing
- The stage now runs `git clone --branch dev …`, then `git checkout bf159d412bb113a52354f73d9def4e5d540ba7fd`, then `test "$(git rev-parse HEAD)" = "bf159d412bb113a52354f73d9def4e5d540ba7fd"` (Dockerfile lines 66–68). The commit-equality guard asserts HEAD == the new pinned SHA.
- Old tag/commit `be2c8de62d27e38c4e886c209eb1fd9fe4fca1f4` is gone from the Dockerfile, patches, NuGet.config, README.md, and Unraid.md. It survives only in prior-task `.agents/tasks/fix-askjaina-voice/*.md` historical records, which are out of scope and correct to leave.
- `DAVE-PR-3288 BEGIN` (line 43) / `END` (line 101) fence a single `AS discordnet` stage; the `build` stage consumes it via `COPY --from=discordnet /packages/` + `COPY patches/NuGet.config`. Cleanup stays a delete-one-stage + delete-patches operation.
- Spot-check: fetched `bf159d41…` from origin — commit exists and resolves (`git rev-parse HEAD` == pinned SHA).

### 2. dave-pr-3288.patch status against dev — correctly handled (kept, applies cleanly, still required)
- Spot-check `git apply --check patches/dave-pr-3288.patch` against the pinned dev checkout: exit 0 (applies cleanly), matching the coder's recorded result.
- Patch intent preserved: `DaveSessionManager.cs` routes `ProcessWelcome`/`ProcessProposals` through `GetRecognizedUserIds() => [.._decryptors.Keys, SelfUserId]`, so the recognized-user set INCLUDES SelfUserId; `Utils.ToCString` reserves the last byte (`span[..^1]`) and writes `span[sz] = 0`, null-terminating the C string.
- "Still required" confirmed from actual dev source at `bf159d41`: `DaveSessionManager.cs:142` still calls `ProcessWelcome(payload, _decryptors.Keys)`, `:173` still passes `_decryptors.Keys` to `ProcessProposals`, and `Utils.cs:28` still does `id.TryFormat(span, out var sz)` with no null terminator. dev does not already contain an equivalent fix, so keeping the patch is correct and dropping it would be wrong.

### 3. Local feed resolves for the app — pack version reconciled
- App `JainaProudmoore.csproj` keeps `<PackageReference Include="Discord.Net" Version="3.20.2" />` (unchanged).
- Spot-check of dev `Discord.Net.targets` at `bf159d41`: `<VersionPrefix>3.20.2</VersionPrefix>`; under `Condition="'$(IsTagBuild)' == 'true'"` with no `BuildNumber`, `VersionSuffix` is empty. So the `dotnet pack … /p:IsTagBuild=true` call packs the components at exactly `3.20.2`, matching the referenced version. Packed version == referenced version — reconciled by the no-bump approach.
- Warning-suppression pack flags preserved: `/p:TreatWarningsAsErrors=false /p:GenerateDocumentationFile=false /p:NoWarn=CS1591%3BNU1510`.
- Choice documented in README.md ("dev packs at 3.20.2, so no app-side version bump is needed"), Dockerfile comments, and Unraid.md.

### 4. Preserved setup — no regression
- Commit touched only Dockerfile/README.md/Unraid.md; app code untouched.
- Dockerfile `base`/`final` stages unchanged: `ARG LIBDAVE_VERSION=1.2.2`, libopus/libsodium unversioned-soname symlinks, ffmpeg install, libdave.so fetch/extract, `FROM base AS final` runtime stage with `ENTRYPOINT ["dotnet", "JainaProudmoore.dll"]`.
- App settings present in tree and untouched: `EnableVoiceDaveEncryption = true`, `BufferMillis = 40`, `selfDeaf: true`, `ConnectStableAsync`.
- net10.0 / -warnaserror-clean app build unaffected (the pack-only warning suppressions are scoped to the throwaway discordnet stage; NuGet.config lives under patches/ and is not applied to the stock local build).

### 5. Documentation
- `patches/README.md`: documents the two batches (dev DAVE fixes + still-open PR #3288), the pinned dev commit `bf159d41`, the exact-SHA pinning rationale, the patch still-required status with per-file detail, the 3.20.2 pack-version note, and the cleanup steps.
- Dockerfile comments: dev-vs-tag rationale, pinned commit, patch-still-required, pack-at-3.20.2, cleanup grep-and-delete.
- `Unraid.md`: "dev @ `bf159d41` + PR #3288" retargeting, the why, patch status, and cleanup pointer.

## Recorded verification (read, not re-run)
Coder's commit message records: shallow clone of dev (HEAD == bf159d41), `git apply --check` PASS (exit 0), pack of Core/Rest/Dave/WebSocket succeeded producing `*.3.20.2.nupkg`, and local `dotnet build JainaProudmoore.csproj -c Release` succeeded with 0 warnings / 0 errors. Live-voice behavior explicitly noted as not verified (requires Unraid docker build + cold /askjaina run) — acceptable, as it is outside what can be verified without the live environment.

My narrow spot-checks (commit existence, VersionPrefix, patch-apply-check, dev source still unfixed) independently corroborate the recorded evidence for the claims that gate correctness.
