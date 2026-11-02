# Jaina Voice Sidecar

A small, **send-only** Discord voice sidecar written in Go. It opens its own
Discord gateway connection with the bot token, exposes a single HTTP endpoint that
accepts a WAV file, joins a voice channel self-deafened, transcodes the WAV to
48kHz stereo Opus, transmits it under Discord's DAVE (end-to-end encryption)
protocol, and then leaves.

It exists because the .NET bot could not reliably send audio under DAVE. This
service owns DAVE voice; the bot keeps the gateway and slash commands and POSTs WAV
bytes here. It never receives or decodes inbound audio.

Built on [`disgoorg/disgo`](https://github.com/disgoorg/disgo) +
[`disgoorg/godave`](https://github.com/disgoorg/godave), with
[`kazzmir/opus-go`](https://github.com/kazzmir/opus-go) for PCM→Opus encoding and
`ffmpeg` for WAV→PCM transcoding. (Upstream facts rephrased for licensing
compliance.)

## Environment variables

| Variable        | Required | Default | Purpose                                                              |
| --------------- | -------- | ------- | -------------------------------------------------------------------- |
| `TOKEN`         | yes      | —       | Discord bot token; the sidecar opens its own gateway with it.        |
| `PORT`          | no       | `8080`  | TCP port the HTTP server binds on `0.0.0.0`.                         |
| `SIDECAR_TOKEN` | no       | (unset) | Shared secret; when set, `/play` requires a matching header (below). |

Startup fails fast with a clear error if `TOKEN` is missing.

## HTTP contract

### `POST /play`

Joins a voice channel and plays the posted WAV, then leaves.

- **Query params**
  - `guildId` — guild snowflake (required)
  - `channelId` — voice-channel snowflake (required)
- **Body** — the raw WAV bytes (no base64). Send `Content-Type: audio/wav` or
  `application/octet-stream`.
- **Header** — `X-Sidecar-Token`: required **only** when `SIDECAR_TOKEN` is set;
  must equal it.

**Status codes**

| Status | Meaning                                                                          |
| ------ | -------------------------------------------------------------------------------- |
| `200`  | Played and left successfully.                                                    |
| `400`  | Missing/invalid `guildId`/`channelId`, or empty body.                            |
| `401`  | `SIDECAR_TOKEN` is set and the `X-Sidecar-Token` header is missing or wrong.     |
| `413`  | Request body exceeds the size cap.                                               |
| `422`  | DAVE never became ready — a stage channel or v0 passthrough (see godave #11).    |
| `502`  | Join/transcode/playback failed.                                                  |
| `504`  | Playback exceeded the bounded timeout.                                           |

### `GET /healthz`

Returns `200 ok`. Used for container health checks.

## Version pairing: disgo ↔ godave ↔ libdave

godave links the native `libdave` library over **CGO (dynamic linking)**, and the
ABI must match the godave version. godave pins its required `libdave` version in
`github.com/disgoorg/godave/libdave/release.txt`.

At the pinned **godave `v0.3.0`**, `release.txt` says **`v1.1.0`**. So this sidecar
downloads and links **libdave `v1.1.0`**.

> **Deviation from the task brief.** The task named libdave `v1.2.2` (the value the
> old .NET bot's Dockerfile used for Discord.Net, a *different* consumer). That does
> not bind this sidecar: godave `v0.3.0` requires libdave `v1.1.0`, and a mismatched
> libdave would fail to link/load. The godave↔libdave pairing wins, so this sidecar
> uses **libdave `v1.1.0`**. If a future godave bump changes `release.txt`, follow
> `release.txt`, not this note.

### disgo pin (and why it is a pseudo-version, not `v0.19.6`)

The official `disgo/_examples/voice/godave` example calls `conn.DAVE().Ready()` —
which this sidecar needs for the godave #11 stage-channel guard. The `DAVE()` method
was added to the `voice.Conn` interface **after** the `v0.19.6` tag and exists only
on `master`; it does **not** compile at `v0.19.6`, even though the example's own
`go.mod` still names `v0.19.6`.

So `disgo` is pinned to the `master` pseudo-version that contains `DAVE()`
(`v0.19.7-0.20261101233420-18ac6df9b116`, commit `18ac6df9b116`), while `godave`
stays at the example's `v0.3.0`. Versions are pinned, not floated.

| Module                            | Version                                   |
| --------------------------------- | ----------------------------------------- |
| `github.com/disgoorg/disgo`       | `v0.19.7-0.20261101233420-18ac6df9b116`   |
| `github.com/disgoorg/godave/golibdave` | `v0.3.0`                             |
| `github.com/disgoorg/godave` (indirect) | `v0.3.0`                            |
| `github.com/disgoorg/godave/libdave` (indirect) | `v0.3.0`                    |
| `github.com/disgoorg/snowflake/v2` | `v2.0.3`                                 |
| `github.com/kazzmir/opus-go`       | `v1.7.0`                                 |
| native `libdave.so`                | `v1.1.0` (from godave `release.txt`)     |

## How `golibdave` loads libdave

`golibdave` uses CGO and dynamically links `libdave.so` at runtime. The build needs
a C toolchain (`CGO_ENABLED=1`) and the libdave headers + shared object present; the
runtime image needs `libdave.so` on the loader path. The Dockerfile places it in
`/usr/local/lib` and runs `ldconfig` (equivalently, set `LD_LIBRARY_PATH`). The
`boringssl` build of libdave statically bundles BoringSSL, so no extra OpenSSL
runtime dependency is needed, but it is a glibc build — hence the Debian-slim
runtime stage (not Alpine/musl).

Two concrete requirements fall out of this and are baked into the Dockerfile:

- **pkg-config.** godave's `libdave/lib.go` resolves the native library with
  `#cgo pkg-config: dave` and `#include "dave.h"`. The libdave release zip ships no
  `dave.pc`, so the build installs `pkg-config`, flattens `dave.h` directly onto the
  include path, writes a `dave.pc` (mirroring godave's `scripts/libdave_install.sh`),
  and sets `PKG_CONFIG_PATH=/usr/local/lib/pkgconfig` before `go build`.
- **Debian trixie, not bookworm.** The prebuilt libdave `v1.1.0` BoringSSL `.so` is
  built against **glibc ≥ 2.38** (`__isoc23_strto*`) and **libstdc++ `GLIBCXX_3.4.32`**
  (`std::ios_base_library_init`). Bookworm (glibc 2.36 / GCC 12) fails to link and
  load it; trixie (glibc 2.41 / GCC 14) satisfies both. The runtime stage installs
  `libstdc++6` (libdave's C++ runtime; BoringSSL is static but the C++ stdlib is not).

## Build and run (container)

```bash
docker build -t jaina-voice-sidecar .
# Fails fast without TOKEN (proves config wiring):
docker run --rm jaina-voice-sidecar
# With a token it binds 0.0.0.0:$PORT and /healthz returns 200:
docker run --rm -e TOKEN=xxxx -p 8080:8080 jaina-voice-sidecar
```

The authoritative build/link check is the Docker build, because CGO + libdave only
link where the C toolchain and `libdave.so` are present. For a fast local check
(static analysis only, no CGO link) run `go vet ./...`.
