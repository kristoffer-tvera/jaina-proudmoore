# How working bots actually SEND voice under Discord DAVE E2EE — field survey and options

READ-ONLY outward-looking research. No code changed. This report answers: *does anyone successfully
send DAVE voice on Discord.Net today, or do working .NET-era voice bots use a different architecture?*
— then enumerates the approaches the field actually uses, with evidence, and lays out the viable
choices for our deliberately-simple use case (play ONE short static WAV, `Algieba.wav`, into the
caller's channel on `/askjaina`, then leave).

All external claims are cited inline. Source content was rephrased for compliance with licensing
restrictions.

---

## Summary answer (bottom line first)

**No public evidence was found of a Discord.Net (C#/.NET-native voice) bot confirmed to SEND audio
under DAVE in production.** The projects that demonstrably send audio under DAVE in 2026 all use a
*different* voice stack than Discord.Net's internal `AudioClient`:

- **Go** — `disgoorg/disgo` + `disgoorg/godave` (CGO bindings to libdave), and the
  `cartridge-gg/discordgo` fork. This is the most mature, most actively maintained, confirmed-working
  DAVE **send** path in the ecosystem (commits through mid-2026, a dedicated DAVE voice example).
- **Rust** — `bongo-devs/voice` (a purpose-built *send* layer) on top of the `davey` DAVE/MLS crate.
- **Python** — `discord.py` 2.x (DAVE-capable), used by real TTS bots that send generated WAVs.
- **JVM** — JDA 6 with DAVE support (e.g. `jagrosh/MusicBot`'s DAVE upgrade PR).
- **A dedicated audio node** — **Lavalink v4.2.0+** officially ships DAVE (E2EE) support via libdave,
  and a .NET bot drives it through the mature **Lavalink4NET** client while keeping Discord.Net only
  for the gateway/slash commands. A DAVE-capable Lavalink-alternative (`ZironDev/Zironlink`, Python)
  also exists but is less proven.

So our current path — **building Discord.Net from source at dev `bf159d41` with two hand patches
(PR #3288 welcome-roster fix + a hand-authored `FinishConnectAudio` same-session guard) and libdave
1.2.2** — is **not** what the working field does. The field either (a) uses a non-.NET DAVE library
that holds the voice connection, or (b) offloads voice to a Lavalink node and keeps the bot on the
gateway only. Our two prior investigations already proved *both* failures we hit are
**library-internal defects in Discord.Net's voice layer that no application code can reach**
(`findings.md`: the MLS welcome-roster recognition gap; `investigation-finishconnectaudio-fix.md`:
the `FinishConnectAudio` teardown introduced by the merged PR #2873). That is consistent with the
field verdict: Discord.Net's voice/DAVE send path is the least-proven of the major ecosystems, and
the working projects route around it.

**Our use case is trivial for every alternative** (one static WAV, send-only, no receive, no music
library, no queue). That strongly favours either the **smallest send-only sidecar** (a tiny Go
process using disgo+godave) or, if you prefer zero new native code and an off-the-shelf server, a
**Lavalink v4 node driven by Lavalink4NET** playing the WAV as a local/HTTP source.

The three options worth putting in front of you are in the table below; none is picked for you.

---

## Options at a glance

| # | Approach | Lang/runtime | Confirmed to SEND under DAVE? | Maturity / activity | Integration effort for OUR bot | UNRAID op cost | Maintenance vs today |
|---|----------|--------------|-------------------------------|---------------------|--------------------------------|----------------|----------------------|
| 1 | **Keep patched Discord.Net from source** (current) | .NET 10 / Discord.Net dev + 2 patches + libdave 1.2.2 | **Not demonstrated by anyone publicly**; our own runs still loop/teardown without the hand patches | dev branch, pre-release DAVE; we carry local patches | zero new moving parts (already built) | one image, already builds from source | **High** — we maintain a fork-in-effect; every upstream bump risks patch drift |
| 2 | **Lavalink v4 node + Lavalink4NET** (sidecar server, bot keeps gateway only) | Java node (Lavalink) + .NET client | **Yes — stock Lavalink v4.2.0+ ships DAVE**; Lavalink is the most battle-tested audio node | Very mature project; DAVE added v4.2.0, libdave 0.1.3 by v4.2.2 | medium — add Lavalink4NET, run a node, play WAV as local/HTTP source; remove our voice code | one extra container (official Lavalink image), modest RAM (JVM) | **Low–medium** — upstream maintains DAVE; we own only config + client wiring |
| 3 | **Tiny send-only Go sidecar** (disgo + godave) | Go process + libdave; our .NET bot controls it | **Yes — disgo+godave is the most proven DAVE send stack**; dedicated DAVE example, active 2026 | Actively maintained through mid-2026; purpose-built for this | medium — write a ~one-command local service (join + play WAV + leave) and a thin IPC from .NET | one small static Go binary/container; tiny footprint | **Low** — the DAVE hard part is upstream's; our sidecar is ~a few hundred lines, send-only |

Secondary mentions (not top-3 for our case): Rust `bongo-devs/voice` (same idea as #3 but Rust),
`ZironDev/Zironlink` (DAVE Lavalink-alternative, but public build is redacted and DAVE "not yet
verified end-to-end"), self-hosted-client / relay patterns (overkill, ToS-risky).

---

## The primary question, with evidence

### Does anyone send DAVE voice on Discord.Net?

I found **no** repo, issue, PR, blog, or doc showing a Discord.Net-native voice bot *confirmed sending*
audio under DAVE in production. What exists instead:

- Discord.Net's own DAVE docs describe only the *library setup* (drop `libdave.dll`/`.so`/`.o` next to
  the executable and it is picked up) and the generic send API (`ConnectAsync` → `IAudioClient` →
  `CreatePCMStream`) — not a worked, confirmed DAVE-send example
  ([libdave guide](https://docs.discordnet.dev/guides/voice/libdave.html),
  [sending-voice guide](https://docs.discordnet.dev/guides/voice/sending-voice.html)).
- The 3.20.x changelog shows DAVE work is **recent and receive-focused**: e.g. v3.20.0 "strip RTP
  padding before DAVE decrypt" (#3263) — i.e. the project was still fixing DAVE *decrypt* plumbing in
  mid-2026 ([Discord.Net CHANGELOG](https://discordnet.dev/CHANGELOG.html)).
- Our own two investigations (`findings.md`, `investigation-finishconnectaudio-fix.md`) establish that
  on dev `bf159d41` the send path requires **two separate internal fixes** just to survive a cold
  connect with a human present — and that the proximate teardown was *introduced* by upstream's own
  merged voice-reconnect PR #2873, which shipped with a reviewer's identical `TaskCanceledException`
  report unaddressed. That is the signature of an immature send path, not a solved one.

**Conclusion:** sending on Discord.Net-native voice under DAVE is, as of this research, an
unproven/bleeding-edge path that we are effectively pioneering via local patches. Every ecosystem that
has a *confirmed* DAVE send story uses a different voice layer.

### Who DOES send under DAVE, and how

The working examples cluster in non-.NET stacks and in the Lavalink node:

- **Go / disgo+godave** — `disgoorg/disgo` "includes a voice package supporting E2EE via DAVE, with
  both GoDave (CGO) and Dave-Go (pure Go) implementations"
  ([disgo docs](https://docsearch.algolia.com/mcp/docs/repo/disgoorg/disgo)). The repo added a
  dedicated **DAVE voice example** on 2026-04-12 and has voice fixes landing through mid-2026
  ([disgo repo listing](https://github.com/disgoorg/disgo)). `disgoorg/godave` is the Go binding to
  libdave ([godave](https://github.com/disgoorg/godave)). Real deployments rely on it: the
  `Stieneee/mumble-discord-bridge` project **switched from discordgo to disgo+godave specifically for
  DAVE E2EE** (commit "Replace discordgo with disgo+godave for DAVE E2EE support", 2026-03-12), and
  has active commits into April 2026 ([mumble-discord-bridge](https://github.com/Stieneee/mumble-discord-bridge)).
- **Go / discordgo fork** — `cartridge-gg/discordgo` is a fork of bwmarrin/discordgo with "native
  Discord DAVE (E2EE voice) support via libdave"
  ([cartridge-gg/discordgo](https://github.com/cartridge-gg/discordgo)).
- **Rust** — `bongo-devs/voice` is a from-scratch **send** layer: "pulls 20 ms Opus frames … wraps
  them in RTP, encrypts them with DAVE … sends them to Discord over UDP," DAVE backed by the `davey`
  crate, gateway v8, with explicit "recovery from an invalid commit or welcome"
  ([bongo-devs/voice](https://github.com/bongo-devs/voice)). This is exactly the send-only shape our
  use case needs, in Rust.
- **Python** — `abishop1990/discord-ai-voice` is a voice bot "built to work with Discord's E2EE (DAVE
  protocol) introduced in March 2026," and its pipeline ends in `FFmpegPCMAudio` → Discord playback
  (i.e. it **sends** generated TTS WAVs) *and* decodes receive — the harder direction
  ([discord-ai-voice](https://github.com/abishop1990/discord-ai-voice)). discord.py is repeatedly
  cited across bug reports as the stack that "handles DAVE correctly" where JS does not
  ([discord.js #11419](https://github.com/discordjs/discord.js/issues/11419)).
- **JVM** — `jagrosh/MusicBot` PR #1857 upgrades to JDA 6 **for DAVE support** to fix the post-March-1
  voice break ([MusicBot #1857](https://github.com/jagrosh/MusicBot/pull/1857)).
- **Lavalink node** — stock Lavalink **v4.2.0 is "the first Lavalink release with DAVE (E2EE voice)
  support"** (PR #1190), and v4.2.2 bundles libdave 0.1.3
  ([Lavalink v4 changelog](https://lavalink.dev/changelog/v4)).

Note the recurring pattern in the *broken* reports: the pain is almost always on **receive/decrypt**
(discord.js `DecryptionFailed`, discord.py `DAVESession` crashes, our own benign receive-decode spam),
because receiving requires verifying *other* members' credentials. **Sending is the easier direction**
— the bot encrypts with its own ratchet — which is why send-only sidecars are small and why the
working stacks all manage it. See the receive-side bug clusters for contrast:
[openclaw #24825](https://github.com/openclaw/openclaw/issues/24825),
[openclaw #22961](https://github.com/openclaw/openclaw/issues/22961),
[discord.js #11419](https://github.com/discordjs/discord.js/issues/11419),
[eris #1609 "No supported voice mode found"](https://github.com/abalabahaha/eris/issues/1609). And
that a non-DAVE send is now hard-rejected is confirmed by DiscordPHP users getting voice WS close
**4017 (E2EE/DAVE protocol required)** ([DiscordPHP #1461](https://github.com/discord-php/DiscordPHP/discussions/1461)).

---

## Approach 1 — Keep patched Discord.Net built from source (our current path)

**What it is.** .NET 10 bot, Discord.Net built from dev `bf159d41`, two local patches
(`dave-pr-3288.patch` welcome-roster fix + hand-authored `FinishConnectAudio` same-session guard),
libdave 1.2.2 prebuilt `.so`, `EnableVoiceDaveEncryption=true`, ffmpeg → 48k stereo s16le PCM stream.

**Who else does this.** No one found publicly. There is no community fork/branch that bundles "the
known-good DAVE send patch set" for Discord.Net. PR #3288 is a real upstream welcome-roster fix, but
the teardown we also had to patch was *introduced* by upstream PR #2873 (merged) and has no upstream
cure ([#2873](https://github.com/discord-net/Discord.Net/pull/2873),
[#3288](https://github.com/discord-net/Discord.Net/pull/3288); analysis in
`investigation-finishconnectaudio-fix.md`). So we are not adopting a shared patch set — we *are* the
patch set.

**Confirmed to send under DAVE?** Only on our own machine, and only after both local patches; the
unpatched dev build loops (MLS welcome) and/or tears down (second `VOICE_SERVER_UPDATE`). Not validated
by any third party.

**Maturity/activity.** Pre-release DAVE on a dev branch. The receive side still had fixes landing in
3.20.x. Our patches ride a moving `dev` HEAD.

**Integration effort.** Zero new moving parts — it already builds and runs.

**UNRAID op cost.** One image; already builds Discord.Net from source in the `discordnet` stage. No new
container.

**Maintenance burden.** **Highest of the three.** We maintain an effective fork: every Discord.Net bump
risks patch drift on `SocketGuild.FinishConnectAudio` / `AudioClient` / `DaveSessionManager`, and the
live-only reproduction (needs a second human in the channel) makes regressions hard to catch in CI
(`findings.md` §6). This is precisely the "sunk-cost polishing" loop you flagged.

**Verdict on (a)/(b)/(c):** **(c)-leaning.** It is *not* what the ecosystem does (so not reasonable to
keep purely for being "standard"), and there is no single well-known patch set to adopt instead of our
hand-rolled guard (so not simply reinventable). It works for us today only by carrying patches the rest
of the field avoids by not using Discord.Net's voice layer at all. Keeping it is a valid choice only if
you value "no new container, all C#, one repo" over maintenance cost.

---

## Approach 2 — Lavalink v4 node + Lavalink4NET (bot keeps the gateway, node owns DAVE voice)

**What it is.** Run a stock **Lavalink v4.2.0+** server as a sidecar. Our .NET bot keeps Discord.Net
**only** for gateway + slash commands and adds the **Lavalink4NET** client; the node performs the voice
gateway handshake, DAVE/MLS E2EE, and RTP sending. We never touch Discord.Net's `AudioClient`.

**Confirmed to send under DAVE?** **Yes.** DAVE is an official, shipped Lavalink feature:
v4.2.0 is "the first Lavalink release with DAVE (E2EE voice) support" (PR #1190); v4.2.1/v4.2.2 track
libdave 0.1.0 → 0.1.3 ([Lavalink v4 changelog](https://lavalink.dev/changelog/v4)). The client must be
DAVE-aware and pass the new voice-state `channelId`, which Lavalink4NET (a maintained v4 client for
Discord.Net/DSharpPlus/NetCord) handles ([Lavalink4NET](https://github.com/lavalink4net/Lavalink4NET)).

**Can it play OUR static WAV (not just YouTube/music)?** **Yes.** Lavalink has a local-file source
(enable the local source in `application.yml` and load the track by full path), plus an HTTP source;
either can serve `Algieba.wav` ([Lavalink Local Files #539](https://github.com/freyacodes/Lavalink/issues/539),
[config reference](https://lavalink.dev/configuration/config/file)). No YouTube/yt-dlp needed.

**Maturity/activity.** Lavalink is the most battle-tested Discord audio node in existence (used by
large music bots for years); DAVE is a first-class, versioned feature with ongoing libdave bumps. This
is the lowest-risk "confirmed working" option.

**Integration effort.** Medium. Remove `VoicePlayback.cs`'s Discord.Net voice usage; add Lavalink4NET;
stand up a node; on `/askjaina` load `Algieba.wav` as a local/HTTP track and play it, then disconnect.
The DAVE complexity leaves our codebase entirely.

**UNRAID op cost.** One extra container (official Lavalink Docker image, a JVM — heavier RAM than a Go
binary, but a known, stable image). This is the "heavier node" the brief warned could be overkill for a
single static WAV — true, but it is the most proven and needs no native code from us.

**Maintenance burden.** Low–medium. Upstream owns DAVE and libdave bumps; we own `application.yml` and
a small amount of Lavalink4NET wiring. No patch drift, no from-source Discord.Net build required for
voice.

**Trade-off vs our simplicity:** a full music node for one 1-shot WAV is arguably over-engineered, but
it buys us "someone else maintains DAVE" and a batteries-included server. If you ever want more than one
clip, it scales for free.

---

## Approach 3 — Tiny send-only sidecar in Go (disgo + godave)

**What it is.** A small, purpose-built local service (a few hundred lines) using `disgoorg/disgo` +
`disgoorg/godave`: it holds the voice connection, joins the caller's channel, plays a handed-in WAV via
disgo's `AudioSender`/`OpusFrameProvider`, and leaves. Our .NET bot stays in charge (it owns the slash
command and the gateway) and tells the sidecar "join guild/channel X, play this file, then leave" over
a local socket/HTTP/named pipe.

**Confirmed to send under DAVE?** **Yes — this is the most proven DAVE send stack in the field.**
disgo's voice package supports E2EE via DAVE (GoDave CGO + pure-Go impl), ships a dedicated DAVE
example, and is actively maintained into mid-2026
([disgo docs](https://docsearch.algolia.com/mcp/docs/repo/disgoorg/disgo),
[disgo repo](https://github.com/disgoorg/disgo), [godave](https://github.com/disgoorg/godave)). Real
projects depend on it for DAVE specifically (mumble-discord-bridge's migration, above). disgo's
`AudioSender` consuming an `OpusFrameProvider` is the exact contract for "feed me Opus frames," and the
companion `disgoorg/audio` module does PCM/WAV → Opus so we hand it `Algieba.wav` with no custom codec
work ([disgo audio_sender pattern](https://github.com/sabafly/disgo/blob/v0.5.1/voice/audio_sender.go),
[disgoorg/audio](https://github.com/disgoorg/audio)).

**Known caveat to design around.** godave #11: on **stage** channels DAVE negotiates version 0
(passthrough) and `Ready()` never flips true, so disgo sends nothing there — a stage-channel edge case,
not a normal voice-channel problem ([godave #11](https://github.com/disgoorg/godave/issues/11)). Our
`/askjaina` targets ordinary voice channels, so this does not bite us; just don't point it at a stage.

**Maturity/activity.** High and current (commits through June 2026). The hard DAVE/MLS code is
upstream's; our sidecar is thin and send-only.

**Integration effort.** Medium. Write the small Go service (join/play/leave + a trivial local control
endpoint) and a thin .NET client call from the `/askjaina` handler. More code than Approach 2's config,
but all of it is small and ours to shape; the DAVE difficulty is not in our code.

**UNRAID op cost.** Lowest of the real options: a single small static Go binary (or tiny scratch/alpine
container) with libdave; negligible RAM/CPU vs a JVM. Fits a one-shot WAV perfectly.

**Maintenance burden.** Low. We track godave/libdave version pairing (the repo documents when a libdave
re-install is needed), but we carry **no patches** and no from-source Discord.Net voice build. The
send-only surface is tiny and stable.

**Rust variant.** If Go is unwelcome, `bongo-devs/voice` + `davey` gives the identical "send-only
sidecar" shape in Rust, explicitly built as a 20 ms-paced Opus→RTP→DAVE sender with commit/welcome
recovery ([bongo-devs/voice](https://github.com/bongo-devs/voice)). Same integration shape as #3.

---

## Approaches considered and set aside

- **`ZironDev/Zironlink`** — a Python, Lavalink-v4-compatible node with DAVE, explicitly designed so
  Lavalink4NET and other clients "point at it like any other Lavalink node." Attractive *in theory*
  (lighter than JVM Lavalink, DAVE built in), **but** the public repo is a **redacted build** with the
  DAVE/voice/RTP bodies stripped ("won't run as-is"; a prebuilt Windows `.exe` is offered instead), and
  it self-declares DAVE "not yet verified end-to-end against a live session in every edge case." It is
  a closed-source `.exe` or a non-runnable reference. Not recommended for an UNRAID-hosted, auditable
  deployment unless you obtain the full source ([Zironlink](https://github.com/ZironDev/Zironlink)). If
  you want the "light DAVE node behind Lavalink4NET" shape, prefer **stock Lavalink v4** (Approach 2),
  which is open and proven.
- **Self-hosted client / voice relay patterns** (e.g. `sealbro/go-discord-caller` broadcasting one
  caller to many channels) — real and DAVE-capable, but a multi-bot relay is far more than a one-shot
  WAV needs, and user-account automation risks ToS issues. Overkill
  ([go-discord-caller](https://github.com/sealbro/go-discord-caller)).

---

## Verdict on our current approach

Against the field, our patched-from-source Discord.Net path is **closest to (c): a path the working
projects avoid.** Evidence:

1. **No one else demonstrably sends DAVE on Discord.Net.** The confirmed senders are Go, Rust, Python,
   JVM, and Lavalink — never Discord.Net-native voice.
2. **There is no shared "known-good" Discord.Net DAVE patch set to adopt** instead of our hand-rolled
   guard. PR #3288 helps the welcome roster; the teardown we patched was *created* by merged PR #2873
   and remains uncured upstream. So the "reinvent it more simply via a well-known fork/branch"
   option (b) is not actually available for Discord.Net.
3. **The two failures we fought are both library-internal and unreachable from app code**
   (`findings.md`, `investigation-finishconnectaudio-fix.md`). That is the hallmark of an immature
   subsystem, and it is exactly why the field offloads voice elsewhere.

That does **not** mean our current build is worthless — it works *for us* with the patches, needs no new
container, and keeps everything in one C# repo. But it is the **highest-maintenance** option and the one
most exposed to upstream churn, which is the trap you named at the outset.

---

## The open choices (you pick; nothing is implemented)

Given the task is deliberately tiny — one static `Algieba.wav`, send-only, ordinary voice channels,
join → play → leave on `/askjaina` — the realistic shortlist is:

- **Choice A — Stay on patched Discord.Net (Approach 1).** Pros: nothing new to stand up, all C#, one
  repo/image. Cons: highest maintenance; we carry patches no one shares; fragile across upstream bumps;
  live-only repro. Effort to continue: ~none now, ongoing patch upkeep.

- **Choice B — Lavalink v4 node + Lavalink4NET (Approach 2).** Pros: DAVE is an official, proven,
  upstream-maintained feature; plays a local/HTTP WAV fine; removes all DAVE code from our repo; most
  battle-tested. Cons: one extra JVM container on UNRAID (heavier RAM); arguably over-built for one
  clip. Effort: medium (add client, run node, wire local-file play + disconnect; delete our voice
  code).

- **Choice C — Tiny send-only Go sidecar with disgo+godave (Approach 3), or the Rust `bongo-devs/voice`
  equivalent.** Pros: the most-proven DAVE *send* stack; smallest footprint (tiny static binary/
  container); no patches, no from-source Discord.Net voice; purpose-fit for one-shot WAV; we keep full
  control from the .NET bot over a local socket/HTTP. Cons: introduces a second language and a small
  service to own; a trivial IPC to design; mind the stage-channel `Ready()` caveat (not relevant to our
  normal-channel use). Effort: medium (write the small sidecar + a thin .NET call).

**If weighting purely for our simplicity:** Choice **C** (tiny Go/Rust send-only sidecar) is the
lightest "confirmed-working" fit, and Choice **B** (Lavalink4NET + Lavalink) is the lowest-effort
"someone else maintains DAVE forever" fit. Choice **A** is the only one with no new process, at the
cost of being the one the rest of the field has routed around.

---

## Sources

Discord/Lavalink/official:
[Lavalink v4 changelog (DAVE in v4.2.0, libdave 0.1.3 in v4.2.2)](https://lavalink.dev/changelog/v4) ·
[Lavalink config reference](https://lavalink.dev/configuration/config/file) ·
[Lavalink Local Files #539](https://github.com/freyacodes/Lavalink/issues/539) ·
[Discord.Net libdave guide](https://docs.discordnet.dev/guides/voice/libdave.html) ·
[Discord.Net sending-voice guide](https://docs.discordnet.dev/guides/voice/sending-voice.html) ·
[Discord.Net CHANGELOG](https://discordnet.dev/CHANGELOG.html) ·
[DAVE whitepaper](https://daveprotocol.com/) ·
[discord/libdave](https://github.com/discord/libdave) ·
[Discord blog: bringing DAVE to all platforms](https://discord.com/blog/bringing-dave-to-all-discord-platforms)

Working DAVE-send stacks:
[disgoorg/disgo](https://github.com/disgoorg/disgo) ·
[disgoorg/godave](https://github.com/disgoorg/godave) ·
[disgoorg/audio](https://github.com/disgoorg/audio) ·
[disgo docs (DAVE E2EE)](https://docsearch.algolia.com/mcp/docs/repo/disgoorg/disgo) ·
[cartridge-gg/discordgo](https://github.com/cartridge-gg/discordgo) ·
[Stieneee/mumble-discord-bridge (migrated to disgo+godave for DAVE)](https://github.com/Stieneee/mumble-discord-bridge) ·
[bongo-devs/voice (Rust send layer)](https://github.com/bongo-devs/voice) ·
[abishop1990/discord-ai-voice (Python TTS send)](https://github.com/abishop1990/discord-ai-voice) ·
[jagrosh/MusicBot #1857 (JDA 6 DAVE)](https://github.com/jagrosh/MusicBot/pull/1857)

.NET Lavalink clients & alternatives:
[Lavalink4NET](https://github.com/lavalink4net/Lavalink4NET) ·
[ZironDev/Zironlink (redacted DAVE node)](https://github.com/ZironDev/Zironlink)

Discord.Net voice internals (our patches):
[Discord.Net PR #2873 (introduced the teardown)](https://github.com/discord-net/Discord.Net/pull/2873) ·
[Discord.Net PR #3288 (welcome roster)](https://github.com/discord-net/Discord.Net/pull/3288)

Receive-side DAVE pain (context for why send is easier):
[discord.js #11419](https://github.com/discordjs/discord.js/issues/11419) ·
[openclaw #24825](https://github.com/openclaw/openclaw/issues/24825) ·
[openclaw #22961](https://github.com/openclaw/openclaw/issues/22961) ·
[godave #11 (stage passthrough Ready())](https://github.com/disgoorg/godave/issues/11) ·
[eris #1609](https://github.com/abalabahaha/eris/issues/1609) ·
[DiscordPHP #1461 (voice close 4017 DAVE required)](https://github.com/discord-php/DiscordPHP/discussions/1461)

_Content from external sources was rephrased/summarized for compliance with licensing restrictions._
