# Unraid Deployment

This is the Jaina Proudmoore Discord worker. It is built from the GitHub source
directly on Unraid over SSH, then run as a container configured through the Unraid
Docker tab. Nothing is pushed to a registry.

The bot provides role toggling (`/ilovechinesewomen`), voice-channel moves
(`/yoink`), cached message edit/delete auditing, and the optional `/askjaina`
Gemini voice command (answers aloud in your voice channel; requires a Gemini API
key). The native deps `/askjaina` needs (ffmpeg, libopus, libsodium, and the DAVE
E2EE library `libdave.so`) are baked into the image by the Dockerfile, so no extra
Unraid configuration is required for them.

Because the repo is **private**, the build clones over SSH; set up a deploy key on
the Unraid host first (see "SSH deploy key" below). Discord also enforces its DAVE
end-to-end-encryption protocol for voice (since 2026-03-01), so the Dockerfile
downloads a pinned prebuilt `libdave.so` and the bot sets
`EnableVoiceDaveEncryption = true`; without both, voice connections are rejected
with gateway close code `4017`.

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


4. Verify: `ssh -T git@github.com` should report successful authentication.

## 1. Build the Image via SSH

Push the changes you want to deploy, then SSH into Unraid and build the worker from
the latest `master` commit. Clone first, then build from the local path. Passing the
git URL straight to `docker build` fails under BuildKit: the HTTPS URL has no
credentials for the private repo, and the `git@` URL needs an SSH agent socket that
isn't present. Cloning on the host uses the deploy key directly, and Docker only
sees local files. The `Dockerfile` lives in the `JainaProudmoore` subdirectory, so
that subdirectory is the build context:

```bash
rm -rf /tmp/jaina && git clone --depth 1 -b master git@github.com:kristoffer-tvera/jaina-proudmoore.git /tmp/jaina
docker build --pull -t jaina-proudmoore /tmp/jaina/JainaProudmoore
```

This builds the checked-in source on `master`; unpushed local changes are not
included.

## 2. Add the Container in Unraid

1. Open the **Docker** tab in Unraid.
2. Click **Add Container**.
3. Set **Name** to `JainaProudmoore`.
4. Set **Repository** to `jaina-proudmoore`.
5. Click **Apply**.

Configure these environment variables:

```text
TOKEN=<Discord bot token>
DOTNET_ENVIRONMENT=Production
GEMINI_API_KEY=<Gemini API key>
```

`TOKEN` is required; startup fails if it is missing. `GEMINI_API_KEY` is
**optional**: the container starts and all other commands work without it. Set it
only to enable `/askjaina`. The audit channel and toggle role IDs default from
`appsettings.json`; override them only if needed with `Bot__AuditChannelId` and
`Bot__ToggleRoleId` (double underscore). Do not publish any ports: the bot makes
only outbound connections to Discord.

## 3. Update After Pushing New Code

When you push new code to GitHub:

1. Re-run the build command over SSH to build the latest `master` source.
2. In Unraid, open **Advanced View** (top right).
3. Click **Force Update** on the container.
