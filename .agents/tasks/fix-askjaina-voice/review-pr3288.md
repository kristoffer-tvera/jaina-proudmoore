# Review — Discord.Net 3.20.2 from-source build with PR #3288 (DAVE voice)

Commit under review: `67e443e` (feat: build Discord.Net 3.20.2 from source with PR #3288).
Files changed: `JainaProudmoore/Dockerfile`, `JainaProudmoore/patches/{dave-pr-3288.patch, NuGet.config, README.md, .gitattributes}`, `Unraid.md`. No app source or csproj touched.

Verdict: APPROVED. No blockers.

## (a) Patch faithfully reproduces PR #3288 — PASS

Fetched the live PR diff (`https://github.com/discord-net/Discord.Net/pull/3288.diff`) and compared to `patches/dave-pr-3288.patch`:

- Both target files present: `src/Discord.Net.WebSocket/Audio/DaveSessionManager.cs` and `tools/Discord.Net.Dave/Utils.cs`.
- Identical blob hashes in both hunk headers (`0b4157ca20..3bbd14b211`, `59a14530b3..4e747e8682`).
- File 1, all three edits:
  - `OnMLSWelcomeAsync`: `ProcessWelcome(payload, _decryptors.Keys)` → `ProcessWelcome(payload, GetRecognizedUserIds())` (with the libdave comment).
  - Proposals handler: `_decryptors.Keys` → `GetRecognizedUserIds()`.
  - Added `private HashSet<ulong> GetRecognizedUserIds() => [.._decryptors.Keys, SelfUserId];`.
- File 2, both edits: `id.TryFormat(span, ...)` → `id.TryFormat(span[..^1], ...)` and `span[sz] = 0;` added before `str = new(ptr, sz);`.

The only textual difference from the GitHub `.diff` is that the repo patch retains the blank context lines (so its hunk headers are `@@ -139,7 +139,8 @@` etc.), which is what makes it directly `git apply`-able; GitHub's rendered `.diff` collapses those.

Spot-check against the real tag:
- Cloned `--branch 3.20.2`; `HEAD = be2c8de62d27e38c4e886c209eb1fd9fe4fca1f4` (matches pinned commit).
- `git apply --check --verbose` of the repo patch: clean on both files, exit 0.
- Applied the patch and confirmed resulting lines: `GetRecognizedUserIds()` at both call sites, the new helper (`=> [.._decryptors.Keys, SelfUserId];`), `TryFormat(span[..^1], ...)`, and `span[sz] = 0;`.
- Compilability: `SelfUserId` is a public property on the same class (`public ulong SelfUserId => _client.Discord.CurrentUser.Id;`) and `System.Collections.Generic` is imported, so the collection expression and `HashSet<ulong>` resolve.

## (b) Isolation = delete-one-stage + remove-one-source — PASS

- Dedicated Docker stage `AS discordnet` fenced by `# ==== DAVE-PR-3288 BEGIN … END ====`; clones the pinned tag, verifies HEAD equals the pinned commit, `git apply`s the patch, and `dotnet pack`s the 7 components at `3.20.2` into `/packages`.
- Every added Dockerfile line is under a DAVE-PR-3288 marker: the whole begin/end block, plus the three build-stage lines (`COPY --from=discordnet …`, `COPY patches/NuGet.config …`) under a `# DAVE-PR-3288:` comment.
- Local feed toggle gated and marked: `patches/NuGet.config` (`<clear/>` then local feed ahead of nuget.org) lives only in `patches/`, is copied into the build context by the Docker build only, and is header-marked DAVE-PR-3288. The stock local/dev `dotnet build` does not see it (not at repo root).
- Stock `PackageReference Include="Discord.Net" Version="3.20.2"` preserved; csproj untouched by the commit.
- `patches/README.md` documents what/why, the pinned commit and PR HEAD, and the exact cleanup steps (grep marker → delete stage + 2 build-stage lines → delete `patches/` → bump csproj). `.gitattributes` pins the patch + NuGet.config to LF so `git apply` works in the Linux build regardless of host autocrlf.

## (c) No regression to existing libdave/DAVE/voice setup — PASS

- Dockerfile diff vs `origin/master` is purely additive. The base stage is unchanged: `LIBDAVE_VERSION=1.2.2`, the libopus/libsodium unversioned-soname symlink loop, ffmpeg install, the libdave fetch, and the `final` runtime stage all preserved verbatim.
- `Worker.cs`: `EnableVoiceDaveEncryption = true` present and unchanged (commit touched no app code).
- `VoicePlayback.cs`: `ConnectStableAsync` and `CreatePCMStream(..., bufferMillis: BufferMillis)` with `BufferMillis = 40` present and unchanged.
- `net10.0` target and the app's Discord.Net public API usage unchanged (no app-side diff).
- App warning-clean under `-warnaserror`: the commit changes no app code, and the coder recorded `dotnet build JainaProudmoore.csproj -c Release -warnaserror` succeeding 0/0 against both the patched local feed and stock nuget.org.

## Recorded verification evidence (accepted, not re-run)

From the commit message: `git apply --check` clean against a fresh 3.20.2 checkout; all 7 `Discord.Net.*` components pack at `3.20.2`; app restores against the local feed + nuget.org with the restored `Discord.Net.Dave` byte-identical to the patched local pack; `-warnaserror` build succeeds 0/0 both ways. The MLS-welcome-completes / audio-actually-plays outcome is correctly called out as only verifiable on the user's Unraid build + live `/askjaina` run.

My independent spot-check (clone pinned tag, verify HEAD, `git apply --check`, apply + inspect, compare to live PR diff) corroborates requirement (a) directly.
