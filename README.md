# Jaina Proudmoore

A .NET 10 Discord worker with role toggling, voice-channel moves, and cached message
edit/delete auditing. Uses Discord.Net 3.20.2.

## Security Before Deployment

Earlier source and Docker launch profiles contained credentials, and a committed
`settings.env` held a live bot token. That file has been removed from the working
tree, but deleting it now does not scrub it from git history. Rotate the Discord bot
token before deploying, and update any deployed secrets. Previously built images and
old logs may also contain credentials. Retire affected images and restrict or remove
those logs per your retention requirements.

Secrets belong in user secrets (for `dotnet run`) or container environment variables
(for Unraid), never source files.

## Build And Test

Install a stable .NET 10 SDK. `global.json` selects the latest installed .NET 10
feature band, without allowing previews or silently switching major versions. The
solution uses the XML-based `.slnx` format. Run from the repository root:

```powershell
dotnet build JainaProudmoore.slnx -c Release -warnaserror
dotnet test JainaProudmoore.slnx -c Release
dotnet publish JainaProudmoore/JainaProudmoore.csproj -c Release -p:UseAppHost=false
```

The tests are offline: no Discord connection or real credentials are needed. Live
command behavior and container execution need separate validation.

## Configuration

The only required value is `TOKEN` (or `DISCORD_TOKEN`), which takes priority over
the equivalent `Bot:Token` setting. A missing token fails startup, as do zeroed
audit-channel or toggle-role IDs.

For local development, use the existing user-secrets ID:

```powershell
dotnet user-secrets set "Bot:Token" "<new-token>" --project JainaProudmoore
dotnet run --project JainaProudmoore
```

This command records the supplied value in shell history. Populate secrets using
your preferred secure workflow if that is unsuitable. User secrets are development
storage, not an encrypted production secret store. For Unraid, the token is set as a
container environment variable in the Docker tab (see [Unraid.md](Unraid.md)).

Nonsecret options live in `JainaProudmoore/appsettings.json`. Environment overrides
use double underscores, for example `Bot__AuditChannelId` or `Bot__ToggleRoleId`.
The audit channel and toggle role keep their original IDs.

## Discord Setup

Enable Message Content Intent in the Discord developer portal. Install the bot
with `bot` and `applications.commands` scopes. Grant access to relevant channels,
View Channel, Read Message History, Send Messages, Manage Roles, and Move Members.
The bot's role must be above the role it toggles. Voice moves also need suitable
destination-channel access. Commands are registered globally when the bot is ready.

- `/ilovechinesewomen`: toggles the configured role, preserving public confirmation.
- `/yoink channel`: moves a snapshot of connected users to another voice channel.
  The caller now needs Move Members permission in both channels; the command's
  default permissions also require Move Members. Same-channel moves are rejected.
- `/askjaina question`: Jaina answers out loud in your current voice channel. You
  must be connected to a voice channel in the server; otherwise the command replies
  ephemerally asking you to join one.

## Ask Jaina (Gemini Voice)

`/askjaina` is powered by Google Gemini and is **optional**: if no API key is set,
the command replies ephemerally that the feature is not configured, and every other
command and the audit watchdog keep working normally. The bot still starts without a
key.

Each successful invocation makes two Gemini calls: one text request
(`generateContent`) to answer in character, and one text-to-speech request to
synthesize that answer. The answer is hard-capped to at most 2-3 short sentences
(instructed in the persona prompt and enforced defensively in code). The bot joins
your voice channel, speaks the answer, then leaves.

Configuration keys (all optional, under the `Bot` section unless noted):

- `GEMINI_API_KEY` (top-level env) or `Bot:GeminiApiKey`: the Gemini API key. The
  env variable wins over the bound `Bot:GeminiApiKey`. Absent means the feature is
  disabled.
- `Bot:GeminiPersonaPrompt`: the system/persona prompt (defaults to an in-character
  Jaina Proudmoore prompt that also constrains answers to 2-3 short sentences).
- `Bot:GeminiVoice`: the TTS voice (default `Kore`).
- `Bot:GeminiTextModel`: the text model (default `gemini-2.5-flash`).
- `Bot:GeminiTtsModel`: the TTS model (default `gemini-2.5-flash-preview-tts`).

The `gemini-3.8-flash` / `gemini-3.8-flash-tts` ids are documented opt-in overrides
for accounts with 3.x model access; set the model keys above to use them.

### Native runtime dependencies

Discord.Net voice needs native `libopus` and `libsodium`, and playback needs
`ffmpeg` on `PATH`. These are installed in the container by the `Dockerfile`. If you
run with `dotnet run` directly, the host must have `libopus`, `libsodium`, and
`ffmpeg` available on `PATH` too.

Audit messages depend on the 1,024-message memory cache: messages not cached before
an edit or deletion cannot be reconstructed. Audit output cannot trigger mentions.
Logs record user IDs, not DM bodies.

## Containers And Deployment

The image runs as the .NET image's non-root user and logs to the console. A running
container is not proof the Discord connection is healthy; inspect logs for connection
and command-registration results. See [Unraid.md](Unraid.md) for the full deployment
steps. There is no registry image and no push step: the container is built from the
GitHub source directly on Unraid.
