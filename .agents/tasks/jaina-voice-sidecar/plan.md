# Implementation Plan — Jaina voice sidecar (Go) + .NET bot rewire + two-container deploy

Chosen path: Approach 3 / Choice C from
`.agents/tasks/fix-askjaina-voice/investigation-working-dave-approaches.md` — a small,
send-only Go sidecar using `disgoorg/disgo` + `disgoorg/godave`. The bot POSTs WAV bytes to the
sidecar over local HTTP; ffmpeg and libdave are bundled in the sidecar container. The fragile
patched-from-source Discord.Net voice path is removed entirely.

This plan is ordered by dependency. The Go sidecar is built and self-contained first (items 1–8),
then the .NET bot is rewired to call it and the Discord.Net voice apparatus is deleted (items 9–15),
then docs/deploy are rewritten (items 16–17). Each item leaves its own project buildable.

## Verified API facts (read from the real sources, do NOT invent)

All confirmed against the real repos on 2026 `master`/pinned tags:

- The official DAVE voice example is `disgoorg/disgo/_examples/voice/godave` (main.go). Its `go.mod`
  pins: `github.com/disgoorg/disgo v0.19.6`, `github.com/disgoorg/godave/golibdave v0.3.0`
  (indirect `github.com/disgoorg/godave v0.3.0`, `github.com/disgoorg/godave/libdave v0.3.0`),
  `github.com/disgoorg/snowflake/v2 v2.0.3`, `go 1.26.0`.
  (Source: https://github.com/disgoorg/disgo/tree/master/_examples/voice/godave — rephrased for
  licensing compliance.)
- DAVE is enabled on the client with
  `bot.WithVoiceManagerConfigOpts(voice.WithDaveSessionCreateFunc(golibdave.NewSession))`. The
  import is `github.com/disgoorg/godave/golibdave`; the file imports `"C"` (CGO).
- Voice send flow (verified in `disgo v0.19.6 voice/conn.go`):
  - `conn := client.VoiceManager.CreateConn(guildID)`
  - `conn.Open(ctx, channelID, selfMute bool, selfDeaf bool)` — **signature confirmed**. For a
    send-only self-deafened bot call `conn.Open(ctx, channelID, false, true)`.
  - `conn.SetSpeaking(ctx, voice.SpeakingFlagMicrophone)`
  - `conn.SetOpusFrameProvider(provider)` where `provider` implements `voice.OpusFrameProvider`
    (`ProvideOpusFrame() ([]byte, error)` returning `io.EOF` when done; `Close()`); the audio sender
    pulls frames from it. `voice.OpusFrameSize` is the per-frame sample count constant.
  - `conn.Close(ctx)` to leave.
- Opus encoding: the official disgo mp3 example encodes PCM→Opus with
  `github.com/kazzmir/opus-go/opus` (`opus.NewEncoder(48000, 2, opus.ApplicationVoIP)`,
  `encoder.Encode(pcmInt16, voice.OpusFrameSize, opusBuf)`). PCM is 48kHz stereo s16le; 16-bit stereo
  = 4 bytes/sample-frame; a frame needs `voice.OpusFrameSize * channels * 2` PCM bytes. This is the
  exact pattern we follow (ffmpeg decodes WAV→PCM, kazzmir/opus-go encodes PCM→Opus).
- Stage-channel caveat (godave issue #11, confirmed): on STAGE channels the voice gateway acks
  `dave_protocol_version: 0` (passthrough), so `DAVE().Ready()` stays false forever and nothing is
  sent. The godave example polls `conn.DAVE().Ready()` before sending. We must bound the wait for
  `Ready()` and return a clear error instead of hanging.
- libdave loading (godave README, confirmed): godave uses **CGO + dynamic linking**; you install
  libdave via `scripts/libdave_install.sh <version>` which downloads the prebuilt lib and places it
  on the loader path. godave pins its required libdave version in
  `github.com/disgoorg/godave/libdave/release.txt` — currently **`v1.1.0`**. (Source:
  https://github.com/disgoorg/godave — rephrased for licensing compliance.)

## Explicit assumptions / risks recorded (verify during implementation)

1. **libdave version conflict (IMPORTANT).** The task says "keep libdave at v1.2.2" (the bot's
   current Dockerfile fetches `discord/libdave` v1.2.2, asset `libdave-Linux-X64-boringssl.zip`).
   But godave v0.3.0 pins libdave **v1.1.0** in `libdave/release.txt`, and godave warns that the
   libdave build must match the GoDave version (ABI/CGO). **Assumption:** the sidecar must use the
   libdave version godave v0.3.0 requires. The coder MUST read
   `github.com/disgoorg/godave/libdave/release.txt` at the pinned godave version and use THAT version
   for the `discord/libdave` release download. If it is not v1.2.2, use the godave-required version
   and document the deviation in the sidecar README (godave↔libdave pairing wins over the task's
   "v1.2.2", because a mismatched libdave will fail to link/load). The bot's v1.2.2 was for
   Discord.Net, a different consumer — it does not bind the sidecar.
2. **`conn.DAVE()` method.** The godave example calls `conn.DAVE().Ready()`, but the `Conn` interface
   text in `disgo v0.19.6 voice/conn.go` that was read did not list a `DAVE()` method in its
   interface block. **Assumption:** `DAVE()` is available at the example's pinned versions (the
   example is in-repo and authoritative). The coder MUST confirm `conn.DAVE().Ready()` compiles at
   the pinned disgo version; if it does not, bump disgo to the minimum version where the godave
   example builds (keep disgo and godave paired as the example's go.mod pairs them) and re-pin. Do
   not float the versions.
3. **go toolchain / Docker not available in the planning environment.** `go` and `gh` are not on
   PATH here, so the Go sidecar's `go build`/`go vet` and both Docker builds were NOT run during
   planning. The coder runs them (see verification lines). CGO + libdave means `go build` must run in
   an environment with the C toolchain and libdave present (the Dockerfile build stage), so the
   authoritative build/verify is the container build, with `go vet ./...` as the fast local check
   where a toolchain exists.
4. **Second gateway connection with the same token.** The task states Discord permits a second
   gateway connection using the same bot token; the sidecar opens its own gateway with `TOKEN`. This
   is taken as given per the task and the investigation; not independently re-verified here.
5. **disgoorg/audio considered, not chosen.** `disgoorg/audio` exists (packages `opus`, `pcm`,
   `samplerate`, `channelconverter`, and a `Player`). We instead mirror the official disgo voice
   examples: ffmpeg does WAV→48kHz-stereo-s16le PCM (the exact, proven transcode the deleted
   `VoicePlayback.cs` used) and `kazzmir/opus-go/opus` does PCM→Opus. Reason: it matches both the
   proven ffmpeg path the user already validated AND the in-repo disgo example, minimizing novel
   surface. `disgoorg/audio` remains a viable alternative if ffmpeg bundling proves undesirable.
6. **Pre-existing test drift (do not "fix" unless it blocks build).** `JainaProudmoore.Tests/BotTests.cs`
   asserts `gemini-2.5-flash` / `gemini-2.5-flash-preview-tts`, but `GeminiDefaults.cs` now declares
   `gemini-3.8-flash` / `gemini-3.8-flash-tts`. This test is already red/out of sync and is unrelated
   to voice. Leave it unless `dotnet test` must pass as a gate — in which case flag it; do not silently
   change model ids.

---

- [ ] 1. Scaffold the Go module in the new sibling directory `JainaVoiceSidecar/`.
      Create `JainaVoiceSidecar/go.mod` with module path `github.com/kristoffer-tvera/jaina-voice-sidecar`
      (matches the repo owner in `Unraid.md`), `go 1.26.0`, and PINNED requires copied from the disgo
      godave example: `github.com/disgoorg/disgo v0.19.6`, `github.com/disgoorg/godave/golibdave v0.3.0`,
      `github.com/disgoorg/snowflake/v2 v2.0.3`, plus `github.com/kazzmir/opus-go v<latest-resolved>`
      (the Opus encoder the disgo mp3 example uses). Do not float versions.
      Files: `JainaVoiceSidecar/go.mod`
      Verify: `cd JainaVoiceSidecar && go mod tidy` resolves without error and writes `go.sum`.

- [ ] 2. Add config loading from env.
      Read `TOKEN` (required), `PORT` (default `8080`), `SIDECAR_TOKEN` (optional shared secret). Fail
      fast with a clear error if `TOKEN` is empty. Keep it a tiny struct + a loader func.
      Files: `JainaVoiceSidecar/config.go`
      Verify: `cd JainaVoiceSidecar && go vet ./...` clean.

- [ ] 3. Create the disgo client with DAVE enabled and open the gateway.
      In `main.go`: `disgo.New(TOKEN, bot.WithGatewayConfigOpts(gateway.WithIntents(gateway.IntentGuildVoiceStates)),
      bot.WithVoiceManagerConfigOpts(voice.WithDaveSessionCreateFunc(golibdave.NewSession)))`, then
      `client.OpenGateway(ctx)`; `client.Close(ctx)` on shutdown. Import `_ "C"` is implied by
      golibdave (CGO). Use `log/slog`. Do NOT set any receive/opus-receiver — send only.
      Files: `JainaVoiceSidecar/main.go`
      Verify: `go vet ./...` clean (full build is gated on CGO+libdave, exercised in item 8/14).

- [ ] 4. Implement the WAV→Opus `voice.OpusFrameProvider` backed by ffmpeg + kazzmir/opus-go.
      One type implementing `ProvideOpusFrame() ([]byte, error)` and `Close()`. It starts
      `ffmpeg -hide_banner -loglevel panic -i pipe:0 -ac 2 -f s16le -ar 48000 pipe:1`, writes the WAV
      bytes to stdin (goroutine, then closes stdin for EOF), reads 48k/stereo/s16le PCM from stdout in
      `voice.OpusFrameSize*2*2`-byte chunks, converts little-endian bytes to `[]int16`, encodes with a
      `opus.NewEncoder(48000, 2, opus.ApplicationVoIP)` into an opus buffer, and returns the frame.
      Return `io.EOF` at end (zero-pad a short final frame like the disgo mp3 example). `Close()` kills
      ffmpeg and closes the encoder. Mirror `disgo/_examples/voice/mp3/main.go` exactly for the
      PCM→Opus mechanics.
      Files: `JainaVoiceSidecar/opusprovider.go`
      Verify: `go vet ./...` clean.

- [ ] 5. Implement the play/join/leave lifecycle with bounded timeouts and the stage-channel guard.
      A `playWav(ctx, guildID, channelID, wav []byte) error` that: `CreateConn(guildID)`;
      `conn.Open(openCtx, channelID, false, true)` (self-deaf, send-only) under a bounded timeout;
      `conn.SetSpeaking(ctx, voice.SpeakingFlagMicrophone)`; polls `conn.DAVE().Ready()` with a bounded
      deadline (e.g. a ticker + timeout context) and, if it never flips (the godave #11 stage/v0
      passthrough case), logs it and returns a clear typed error ("DAVE not ready — stage channel or v0
      passthrough; refusing to send"); on Ready, `conn.SetOpusFrameProvider(provider)` and wait for the
      provider to reach EOF (bounded by a max-playback timeout); ALWAYS `conn.Close(leaveCtx)` in a defer
      so the channel is left even on failure. Every step has its own bounded context so a stuck request
      cannot wedge the service.
      Files: `JainaVoiceSidecar/play.go`
      Verify: `go vet ./...` clean.

- [ ] 6. Implement the HTTP server and `POST /play` on `0.0.0.0:$PORT`.
      Bind `0.0.0.0:PORT`. `POST /play` accepts `guildId` and `channelId` as query params (snowflakes)
      and the WAV as the raw request body (`Content-Type: audio/wav` or `application/octet-stream`) —
      raw body is the simplest robust option (document it; no base64 bloat). If `SIDECAR_TOKEN` is set,
      require header `X-Sidecar-Token` to match and return 401 otherwise. Parse snowflakes with
      `snowflake.Parse`; 400 on bad input; 200 on success; 502/504 on play/transcode failure; 409 (or
      422) with the stage/not-ready message for the godave #11 case. Add a trivial `GET /healthz`
      returning 200. Call `playWav` with a per-request bounded context.
      Files: `JainaVoiceSidecar/server.go`, wire into `JainaVoiceSidecar/main.go`
      Verify: `go vet ./...` clean; `go build ./...` on a host with CGO+libdave (or defer to item 8).

- [ ] 7. Write the sidecar README.
      Document: what it is (send-only DAVE voice sidecar), env vars (`TOKEN`, `PORT`, `SIDECAR_TOKEN`),
      the `/play` contract (method, query params, raw WAV body, `X-Sidecar-Token`, status codes), the
      godave↔libdave version pairing (state the exact libdave version from
      `godave/libdave/release.txt` at godave v0.3.0 — see assumption 1), how `golibdave` loads libdave
      (CGO dynamic link; `LD_LIBRARY_PATH` / loader path), and how to build/run the container.
      Files: `JainaVoiceSidecar/README.md`
      Verify: manual read-through; links resolve; version numbers match `go.mod` and the Dockerfile.

- [ ] 8. Write the multi-stage sidecar Dockerfile (Go build + ffmpeg + libdave runtime).
      Build stage on a Go 1.26 image with the C toolchain (CGO enabled), git/curl/unzip, build the
      static-ish binary with libdave headers/lib present (run godave's `scripts/libdave_install.sh` for
      the godave-required version, OR replicate the bot Dockerfile's fetch of
      `discord/libdave` release `libdave-Linux-X64-boringssl.zip` extracting `lib/libdave.so` — using
      the version from assumption 1). Runtime stage on a small Debian/Alpine base with `ffmpeg`,
      `ca-certificates`, and `libdave.so` placed on the loader path (set `LD_LIBRARY_PATH` or drop it in
      `/usr/local/lib` + `ldconfig`). Keep the license texts for the redistributed libdave binary. Copy
      the binary; `EXPOSE 8080`; `ENTRYPOINT` the binary. Match Alpine vs glibc to how libdave was built
      (BoringSSL static → prefer glibc Debian slim unless the MUSL libdave build is used).
      Files: `JainaVoiceSidecar/Dockerfile`, `JainaVoiceSidecar/.dockerignore`
      Verify: `docker build -t jaina-voice-sidecar JainaVoiceSidecar` succeeds; `docker run --rm
      jaina-voice-sidecar` starts and logs the bound port (without TOKEN it should fail fast with the
      config error — that proves config wiring).

- [ ] 9. Add bot config for the sidecar.
      Add `SidecarBaseUrl` (default `http://jaina-voice-sidecar:8080`) and `SidecarToken` (default
      empty) to `BotOptions`. Bind from `Bot:SidecarBaseUrl` / `Bot:SidecarToken` (env
      `Bot__SidecarBaseUrl` / `Bot__SidecarToken`). Keep all existing options intact.
      Files: `JainaProudmoore/BotOptions.cs`
      Verify: `dotnet build JainaProudmoore.slnx -c Release -warnaserror` succeeds.

- [ ] 10. Register the named HttpClient `"voice-sidecar"`.
      In `Program.cs`, add `builder.Services.AddHttpClient("voice-sidecar", (sp, http) => { base
      address = BotOptions.SidecarBaseUrl; a generous Timeout (e.g. 120s to cover join+play+leave);
      if SidecarToken set, add header X-Sidecar-Token })`. Use the injected `IHttpClientFactory`
      already present in `Worker`.
      Files: `JainaProudmoore/Program.cs`
      Verify: `dotnet build … -warnaserror` succeeds.

- [ ] 11. Rewire `RunAskJainaAsync` to POST WAV bytes to the sidecar instead of `VoicePlayback`.
      Replace the `new VoicePlayback(_logger).PlayWavAsync(...)` call (static-clip branch) with a POST
      to the `"voice-sidecar"` client: `POST {SidecarBaseUrl}/play?guildId={guild}&channelId={channel}`
      with the WAV bytes as a `ByteArrayContent` (media type `audio/wav`). Resolve `guildId` from
      `command.GuildId` and `channelId` from `voiceChannel.Id`. Throw on non-success status so the
      existing catch sets the failure `ModifyOriginalResponseAsync`. Keep the commented Gemini block,
      but update its comment so the ONLY difference when re-enabling AI is which `wav` bytes are sent
      (both paths now hit the same sidecar POST) — extract a small local `PlayViaSidecarAsync(wav)`
      helper the static branch calls and the commented block references, so re-enabling Gemini is
      swapping the byte source. Preserve the success `ModifyOriginalResponseAsync("Here is Jaina.")`.
      Files: `JainaProudmoore/Worker.cs`
      Verify: `dotnet build … -warnaserror` succeeds; `dotnet test JainaProudmoore.slnx -c Release`
      (note pre-existing BotTests drift, assumption 6).

- [ ] 12. Remove `EnableVoiceDaveEncryption` and the voice intents/config the bot no longer needs.
      In `Worker.cs` delete `EnableVoiceDaveEncryption = true` from the `DiscordSocketConfig` (the bot
      no longer connects voice). Leave `GatewayIntents` as-is unless a voice-only intent was added
      solely for playback (none was — keep `AllUnprivileged | MessageContent`). Confirm it compiles
      without the voice-encryption flag.
      Files: `JainaProudmoore/Worker.cs`
      Verify: `dotnet build … -warnaserror` succeeds.

- [ ] 13. Delete `VoicePlayback.cs` and remove all now-dead references.
      Delete `JainaProudmoore/VoicePlayback.cs`. Remove the `using Discord.Audio;` and any other
      references that only existed for it. Confirm no other file references `VoicePlayback`,
      `CreatePCMStream`, `IAudioClient`, or `ConnectAsync` for voice.
      Files: delete `JainaProudmoore/VoicePlayback.cs`; edit `JainaProudmoore/Worker.cs` if needed
      Verify: `grep -r "VoicePlayback\|CreatePCMStream\|IAudioClient" JainaProudmoore` returns nothing;
      `dotnet build … -warnaserror` succeeds.

- [ ] 14. Remove the patched-from-source Discord.Net apparatus from the bot Dockerfile.
      In `JainaProudmoore/Dockerfile`: delete the entire fenced `DAVE-PR-3288 BEGIN … END`
      `discordnet` build stage; delete the two `DAVE-PR-3288` lines in the `build` stage
      (`COPY --from=discordnet /packages/ ./local-packages/` and `COPY patches/NuGet.config
      ./NuGet.config`). The bot returns to stock Discord.Net 3.20.2 from nuget.org (the
      PackageReference is already 3.20.2 and unchanged). Also remove the libdave.so fetch and the
      libopus/libsodium unversioned-symlink block, and ffmpeg, from the `base` stage — the bot no
      longer does voice, so none are needed (verified: only `VoicePlayback.cs` used ffmpeg/libopus/
      libsodium/libdave, and it is deleted). Keep `ca-certificates`. Keep the non-root `USER` switch.
      Files: `JainaProudmoore/Dockerfile`
      Verify: `docker build -t jaina-proudmoore JainaProudmoore` succeeds and the image has no
      `local-packages`/`NuGet.config`/`libdave.so`; bot still starts (fails fast only on missing TOKEN).

- [ ] 15. Delete the `patches/` directory.
      Delete `JainaProudmoore/patches/` entirely (`dave-pr-3288.patch`,
      `dave-finishconnectaudio-guard.patch`, `NuGet.config`, `README.md`, `.gitattributes`). Confirm
      nothing references them (the Dockerfile stopped after item 14).
      Files: delete `JainaProudmoore/patches/`
      Verify: `grep -r "DAVE-PR-3288\|dave-pr-3288\|dave-finishconnectaudio\|local-packages" .` returns
      nothing outside `.agents/`; `docker build -t jaina-proudmoore JainaProudmoore` still succeeds.

- [ ] 16. Rewrite `Unraid.md` for the two-container deployment.
      Keep it at the repo root (`f:\git\jaina-proudmoore\Unraid.md`) — one file is cleaner for a
      two-service setup than splitting. Document: build BOTH images from GitHub source on Unraid via
      the existing clone-then-build SSH-deploy-key pattern (bot from the `JainaProudmoore` subdir,
      sidecar from the `JainaVoiceSidecar` subdir); put both containers on a shared Docker network so
      the bot reaches the sidecar by container name (`jaina-voice-sidecar`); env vars — bot: `TOKEN`,
      `DOTNET_ENVIRONMENT`, `GEMINI_API_KEY`, `Bot__SidecarBaseUrl`
      (`http://jaina-voice-sidecar:8080`), optional `Bot__SidecarToken`; sidecar: `TOKEN`, `PORT`,
      optional `SIDECAR_TOKEN` (must equal the bot's `Bot__SidecarToken`); note no ports need
      publishing externally. Remove the entire `DAVE-PR-3288`/patched-Discord.Net blockquote. Add a
      short WHY: Discord.Net could not reliably send under DAVE, so the Go sidecar owns DAVE voice
      while the bot keeps gateway + slash commands. Update the native-deps line (bot no longer needs
      ffmpeg/libopus/libsodium/libdave; the sidecar bundles ffmpeg + libdave).
      Files: `f:\git\jaina-proudmoore\Unraid.md`
      Verify: manual read-through; both build commands name the correct subdirs; env var names match
      items 2/9; no `DAVE-PR-3288` references remain.

- [ ] 17. Update the root `README.md` voice/deps sections to match the new architecture.
      Update "Native runtime dependencies" (bot no longer needs libopus/libsodium/ffmpeg) and the
      `/askjaina` description to note it delegates audio to the sidecar. Keep all other sections. This
      keeps the repo docs internally consistent with `Unraid.md`.
      Files: `f:\git\jaina-proudmoore\README.md`
      Verify: manual read-through; consistent with `Unraid.md` and `JainaVoiceSidecar/README.md`.

## Digest

Files touched, the exact block/signature to change, its covering test, and what was verified. The
coder opens a file only to edit it.

### NEW — `JainaVoiceSidecar/` (Go module)
- `go.mod` — module `github.com/kristoffer-tvera/jaina-voice-sidecar`, `go 1.26.0`; pinned requires
  `disgo v0.19.6`, `godave/golibdave v0.3.0` (+ indirect `godave v0.3.0`, `godave/libdave v0.3.0`),
  `snowflake/v2 v2.0.3`, `kazzmir/opus-go` (resolved pin). Verify: `go mod tidy`.
- `config.go` — `type config struct{ Token, Port, SidecarToken string }` + `loadConfig() (config,
  error)`; fail if `Token==""`; `Port` default `"8080"`. Verify: `go vet ./...`.
- `main.go` — `disgo.New(... WithDaveSessionCreateFunc(golibdave.NewSession))`, `OpenGateway`, start
  HTTP server, block on signal, `client.Close` on shutdown. Verify: `go vet ./...`, container build.
- `opusprovider.go` — `type wavOpusProvider` implementing `voice.OpusFrameProvider`
  (`ProvideOpusFrame() ([]byte, error)`, `Close()`); ffmpeg `-i pipe:0 -ac 2 -f s16le -ar 48000
  pipe:1`; encode with `opus.NewEncoder(48000, 2, opus.ApplicationVoIP)` + `voice.OpusFrameSize`.
  Mirrors `disgo/_examples/voice/mp3/main.go`. Verify: `go vet ./...`.
- `play.go` — `playWav(ctx, guildID, channelID snowflake.ID, wav []byte) error`: `CreateConn` →
  `conn.Open(ctx, channelID, false, true)` → `SetSpeaking` → bounded `conn.DAVE().Ready()` poll
  (typed error on timeout per godave #11) → `SetOpusFrameProvider` → wait EOF (bounded) → defer
  `conn.Close`. Verify: `go vet ./...`, live container smoke test.
- `server.go` — `POST /play?guildId=&channelId=` raw WAV body; optional `X-Sidecar-Token`; status
  codes 200/400/401/409|422/502/504; `GET /healthz`. Verify: `go vet ./...`; `docker run` healthz.
- `README.md`, `Dockerfile`, `.dockerignore` — doc + multi-stage build + ffmpeg/libdave runtime.
  Verify: `docker build -t jaina-voice-sidecar JainaVoiceSidecar`.
- No Go unit-test framework exists in this new module; verification is `go vet ./...` + `go build`
  (CGO, in the Docker build) + a `docker run` healthz/config-fail smoke check. Add a table-driven
  `TestLoadConfig` in `config_test.go` if quick (`go test ./...`) — optional, not required to build.

### `JainaProudmoore/BotOptions.cs`
- Add `public string SidecarBaseUrl { get; set; } = "http://jaina-voice-sidecar:8080";` and
  `public string SidecarToken { get; set; } = string.Empty;` alongside the existing options.
- Covering test: `JainaProudmoore.Tests/BotTests.cs` (options-default tests). Optionally add an
  assertion for the new default URL. Verify: `dotnet build JainaProudmoore.slnx -c Release -warnaserror`.

### `JainaProudmoore/Program.cs`
- After the existing `AddHttpClient("gemini", …)` block, add `AddHttpClient("voice-sidecar", (sp,
  http) => { http.BaseAddress = new Uri(opts.SidecarBaseUrl); http.Timeout = TimeSpan.FromSeconds(120);
  if (!string.IsNullOrWhiteSpace(opts.SidecarToken)) http.DefaultRequestHeaders.TryAddWithoutValidation(
  "X-Sidecar-Token", opts.SidecarToken.Trim()); })`. Verify: build `-warnaserror`.

### `JainaProudmoore/Worker.cs`
- `DiscordSocketConfig` initializer: remove the `EnableVoiceDaveEncryption = true` line (and its
  comment). `GatewayIntents` unchanged.
- `RunAskJainaAsync`: replace `await new VoicePlayback(_logger).PlayWavAsync(voiceChannel, wav,
  _stoppingToken);` with a `await PlayViaSidecarAsync(command.GuildId!.Value, voiceChannel.Id, wav);`
  helper that uses `_httpClientFactory.CreateClient("voice-sidecar")` to POST the WAV to
  `/play?guildId=…&channelId=…` and throws on non-success. Update the commented Gemini block to note
  it only swaps the `wav` source. Keep both `ModifyOriginalResponseAsync` calls and the catch.
- Remove `using Discord.Audio;` if present once `VoicePlayback` is gone.
- Covering test: none direct (voice path is integration-only); build + `dotnet test` (pre-existing
  BotTests drift noted). Verify: `dotnet build … -warnaserror`.

### DELETE `JainaProudmoore/VoicePlayback.cs`
- Entire file (the Discord.Net `IAudioClient`/`CreatePCMStream` playback). No test references it.
  Verify: grep finds no `VoicePlayback`/`IAudioClient`/`CreatePCMStream`; build `-warnaserror`.

### `JainaProudmoore/Dockerfile`
- Delete the fenced `# ==== DAVE-PR-3288 BEGIN … END ====` `discordnet` stage.
- In `build` stage delete `COPY --from=discordnet /packages/ ./local-packages/` and
  `COPY patches/NuGet.config ./NuGet.config`.
- In `base` stage delete the libopus0/libsodium install + unversioned-symlink loop, the libdave fetch
  (curl/unzip + `ARG LIBDAVE_*`), and `ffmpeg`. Keep `ca-certificates`, the non-root `USER $APP_UID`.
- Verify: `docker build -t jaina-proudmoore JainaProudmoore` succeeds; stock Discord.Net 3.20.2
  resolves from nuget.org.

### DELETE `JainaProudmoore/patches/`
- `dave-pr-3288.patch`, `dave-finishconnectaudio-guard.patch`, `NuGet.config`, `README.md`,
  `.gitattributes`. Verify: grep finds no `DAVE-PR-3288`/`local-packages` outside `.agents/`.

### `Unraid.md` (repo root) and `README.md` (repo root)
- Rewrite `Unraid.md` for two containers on a shared network (build both from GitHub subdirs; env
  vars per item 16; no external ports; remove the patched-Discord.Net blockquote; add WHY).
- Update `README.md` native-deps + `/askjaina` sections to match. Verify: manual read-through,
  consistent env var names and build commands.

### What was verified during planning
- Read: `Worker.cs`, `VoicePlayback.cs`, `GeminiDefaults.cs`, `BotOptions.cs`, `Program.cs`,
  `JainaProudmoore.csproj`, `appsettings.json`, bot `Dockerfile`, `patches/README.md`, root
  `README.md`, root `Unraid.md`, `BotTests.cs`, and the investigation report.
- Read the real disgo/godave API: the `disgoorg/disgo/_examples/voice/godave` example (`main.go`,
  `go.mod`), the `mp3` example `main.go`, `disgo v0.19.6 voice/conn.go` (confirmed the `Conn`
  interface + `Open(ctx, channelID, selfMute, selfDeaf)` signature), the godave README, and
  `godave/libdave/release.txt` (libdave pin). Content rephrased for licensing compliance.
- NOT run in the planning environment (no `go`/`gh`/toolchain, no Docker here): `go build`/`go vet`,
  both `docker build`s, `dotnet build`/`dotnet test`. These are the coder's verification gates.
- Open risks the coder MUST resolve at build time: libdave version pairing (assumption 1),
  `conn.DAVE()` availability at the pinned disgo version (assumption 2).
