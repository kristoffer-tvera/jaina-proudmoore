# Unraid Deployment

Jaina Proudmoore runs as **two containers** on Unraid:

- **`jaina-proudmoore`** — the .NET 10 Discord worker. It owns the gateway
  connection and all slash commands (`/ilovechinesewomen`, `/yoink`, the audit
  watchdog, and `/askjaina`). It no longer connects to voice itself.
- **`jaina-voice-sidecar`** — a small Go send-only voice service. It owns Discord's
  DAVE (end-to-end-encryption) voice path: the bot POSTs WAV bytes to it, and the
  sidecar joins the voice channel, transmits the audio under DAVE, and leaves.

Both images are built from the GitHub source directly on Unraid over SSH, then run
as containers configured through the Unraid Docker tab. Nothing is pushed to a
registry.

**Why two containers?** Discord.Net could not reliably *send* audio under Discord's
mandatory DAVE end-to-end encryption, so the Go sidecar (built on `disgoorg/disgo` +
`disgoorg/godave`) owns DAVE voice while the bot keeps the gateway and slash
commands. The bot hands the sidecar WAV bytes over HTTP and the sidecar does the
voice work. See [`JainaVoiceSidecar/README.md`](JainaVoiceSidecar/README.md) for the
sidecar's `/play` contract and the godave↔libdave version pairing.

## Native runtime dependencies

The split moves all the native voice dependencies into the sidecar image:

- The **bot** image needs no native voice libraries anymore — no `ffmpeg`, no
  `libopus`, no `libsodium`, no `libdave.so`. It runs on stock Discord.Net 3.20.2
  from nuget.org.
- The **sidecar** image bundles `ffmpeg` (WAV→PCM transcode) and the pinned
  `libdave.so` (DAVE E2EE). Its Dockerfile installs both, so no extra Unraid
  configuration is required for them.

Because the repo is **private**, the build clones over SSH; set up a deploy key on
the Unraid host first (see "SSH deploy key" below). The build host needs outbound
HTTPS to github.com (both images fetch from GitHub during build; the sidecar also
downloads the pinned `libdave.so`).

## 0. SSH deploy key (one-time, private repo)

The repo is private, so the build must authenticate to GitHub. Unraid rebuilds
`/root` on every boot, so persist the key on the flash drive and restore it at boot.

1. Generate a key and print the public half:

   ```bash
   mkdir -p /boot/config/ssh
   ssh-keygen -t ed25519 -C "jaina-unraid-deploy" -f /boot/config/ssh/id_ed25519 -N ""
   cat /boot/config/ssh/id_ed25519.pub
   ```

2. Add the printed public key to the repo on GitHub under
   **Settings -> Deploy keys -> Add deploy key** (read-only access is enough).

3. Verify: `ssh -T git@github.com` should report successful authentication.

## 1. Build Both Images via SSH

Push the changes you want to deploy, then SSH into Unraid and build both images from
the latest `master` commit. Clone once, then build each image from its subdirectory.
Passing the git URL straight to `docker build` fails under BuildKit: the HTTPS URL
has no credentials for the private repo, and the `git@` URL needs an SSH agent
socket that isn't present. Cloning on the host uses the deploy key directly, and
Docker only sees local files.

Each service has its own `Dockerfile` in its own subdirectory, so each subdirectory
is its own build context — the bot from `JainaProudmoore`, the sidecar from
`JainaVoiceSidecar`:

```bash
rm -rf /tmp/jaina && git clone --depth 1 -b master git@github.com:kristoffer-tvera/jaina-proudmoore.git /tmp/jaina
docker build --pull -t jaina-proudmoore /tmp/jaina/JainaProudmoore
docker build --pull -t jaina-voice-sidecar /tmp/jaina/JainaVoiceSidecar
```

This builds the checked-in source on `master`; unpushed local changes are not
included.

## 2. Shared Docker Network

The bot reaches the sidecar by container name over a private Docker network — no
ports are published to the host. Create a user-defined bridge network once and put
both containers on it so Docker's embedded DNS resolves the sidecar's container name:

```bash
docker network create jaina-net
```

In the Unraid **Docker** tab, set each container's **Network Type** to `jaina-net`
(it appears in the dropdown once created). With both on `jaina-net`, the bot reaches
the sidecar at `http://jaina-voice-sidecar:8080`.

## 3. Add the Sidecar Container in Unraid

1. Open the **Docker** tab in Unraid.
2. Click **Add Container**.
3. Set **Name** to `jaina-voice-sidecar` (the name the bot uses to reach it — it
   must match exactly).
4. Set **Repository** to `jaina-voice-sidecar`.
5. Set **Network Type** to `jaina-net`.
6. Click **Apply**.

Configure these environment variables:

```text
TOKEN=<Discord bot token>
PORT=8080
SIDECAR_TOKEN=<optional shared secret>
```

- `TOKEN` is the **same** Discord bot token as the bot uses — Discord permits a
  second gateway connection for the same bot. Startup fails fast if it is missing.
- `PORT` is optional (default `8080`); the server binds `0.0.0.0:$PORT`.
- `SIDECAR_TOKEN` is optional. When set, `/play` requires a matching
  `X-Sidecar-Token` header, so it **must equal** the bot's `Bot__SidecarToken`. Leave
  both unset to run without the shared secret.

Do not publish any ports: the sidecar is reached only by the bot over `jaina-net`,
and it makes only outbound connections to Discord.

## 4. Add the Bot Container in Unraid

1. In the **Docker** tab, click **Add Container** again.
2. Set **Name** to `JainaProudmoore`.
3. Set **Repository** to `jaina-proudmoore`.
4. Set **Network Type** to `jaina-net`.
5. Click **Apply**.

Configure these environment variables:

```text
TOKEN=<Discord bot token>
DOTNET_ENVIRONMENT=Production
GEMINI_API_KEY=<Gemini API key>
Bot__SidecarBaseUrl=http://jaina-voice-sidecar:8080
Bot__SidecarToken=<optional shared secret>
```

- `TOKEN` is required; startup fails if it is missing.
- `GEMINI_API_KEY` is **optional**: the container starts and all other commands work
  without it. Set it only to enable `/askjaina`.
- `Bot__SidecarBaseUrl` points at the sidecar by its container name on `jaina-net`;
  the default baked into the image is already `http://jaina-voice-sidecar:8080`, so
  you only need to set it if you renamed the sidecar container or changed its port.
- `Bot__SidecarToken` is optional and must equal the sidecar's `SIDECAR_TOKEN` when
  that is set (double underscore maps to the `Bot:SidecarToken` config key).

The audit channel and toggle role IDs default from `appsettings.json`; override them
only if needed with `Bot__AuditChannelId` and `Bot__ToggleRoleId` (double
underscore). Do not publish any ports: the bot makes only outbound connections to
Discord and talks to the sidecar over `jaina-net`.

## 5. Update After Pushing New Code

When you push new code to GitHub:

1. Re-run the build commands over SSH (step 1) to build the latest `master` source.
   Rebuild whichever image(s) changed — both share the same clone.
2. In Unraid, open **Advanced View** (top right).
3. Click **Force Update** on each container you rebuilt.
