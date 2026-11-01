# Unraid Deployment

This is the Jaina Proudmoore Discord worker. It is built from the GitHub source
directly on Unraid over SSH, then run as a container configured through the Unraid
Docker tab. Nothing is pushed to a registry.

The bot provides role toggling (`/ilovechinesewomen`), voice-channel moves
(`/yoink`), cached message edit/delete auditing, and the optional `/askjaina`
Gemini voice command (answers aloud in your voice channel; requires a Gemini API
key). The native deps `/askjaina` needs (ffmpeg, libopus, libsodium) are baked into
the image by the Dockerfile, so no extra Unraid configuration is required for them.

## 1. Build the Image via SSH

Push the changes you want to deploy, then SSH into Unraid and build the worker from
the latest `master` commit. The `Dockerfile` lives in the `JainaProudmoore`
subdirectory, so the build context is set with the `:JainaProudmoore` suffix:

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
