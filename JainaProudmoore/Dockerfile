FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app
# Native deps for Discord.Net voice, installed as root before the USER switch:
#   - libopus / libsodium: audio codec + encryption. Debian ships only the versioned sonames
#     (libopus.so.0, libsodium.so.NN); Discord.Net's P/Invoke asks the loader for the
#     UNVERSIONED names (libopus.so, libsodium.so), which otherwise exist only in the -dev
#     packages. We symlink the unversioned names so the loader resolves them at runtime.
#   - ffmpeg: playback transcoding, needs to be on PATH.
#   - libdave.so: Discord enforces the DAVE (E2EE) voice protocol as of 2026-03-01. We fetch
#     the prebuilt Linux x64 release binary (BoringSSL statically bundled, so no extra OpenSSL
#     runtime dep) and place it in the app dir where Discord.Net loads it. Bump the tag below
#     to upgrade DAVE support.
# libsodium soname varies by Debian release, so its install is resilient across candidates.
ARG LIBDAVE_VERSION=1.2.2
ARG LIBDAVE_ASSET=libdave-Linux-X64-boringssl.zip
RUN set -eux \
    && apt-get update \
    && apt-get install -y --no-install-recommends libopus0 ffmpeg curl unzip ca-certificates \
    && ( apt-get install -y --no-install-recommends libsodium23 \
      || apt-get install -y --no-install-recommends libsodium26 \
      || apt-get install -y --no-install-recommends 'libsodium*' ) \
    && for name in libopus libsodium; do \
         lib="$(find / -name "$name.so.*" -type f 2>/dev/null | head -n1)"; \
         if [ -n "$lib" ]; then ln -sf "$lib" "$(dirname "$lib")/$name.so"; fi; \
       done \
    # Discord enforces DAVE (E2EE) for voice; Discord.Net loads libdave.so from the app dir.
    # The zip holds lib/libdave.so plus headers (unused) and licenses. Extract the .so flat
    # into /app, and keep the license texts since we redistribute the binary in the image.
    && curl -fsSL -o /tmp/libdave.zip \
         "https://github.com/discord/libdave/releases/download/v${LIBDAVE_VERSION}/cpp/${LIBDAVE_ASSET}" \
    && unzip -j -o /tmp/libdave.zip 'lib/libdave.so' -d /app \
    && unzip -o /tmp/libdave.zip 'licenses/*' -d /app/libdave-licenses \
    && rm -f /tmp/libdave.zip \
    && test -f /app/libdave.so \
    # curl/unzip were only needed for the libdave fetch; drop them to keep the image lean.
    # Keep ca-certificates (TLS to Discord/Gemini at runtime) by purging only these two.
    && apt-get purge -y curl unzip \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/* \
    && ldconfig
USER $APP_UID

# ==== DAVE-PR-3288 BEGIN (from-source Discord.Net build) ====================
# TEMPORARY: builds Discord.Net from source at a PINNED dev-branch commit with
# approved-but-unmerged PR #3288 applied, so /askjaina DAVE voice actually works.
# We build from dev (not the 3.20.2 release tag) because dev carries a batch of DAVE
# voice fixes absent from 3.20.2 (RTP padding #3263, multi-user voice fixes, etc.)
# that are needed for the /askjaina teardown + silent-audio-with-2+-users symptoms.
# PR #3288 (the welcome-roster / null-terminator fix) is STILL open and NOT merged
# into dev, so its patch is still required and is applied on top of the dev checkout.
#   PR:   https://github.com/discord-net/Discord.Net/pull/3288
#   dev:  commit bf159d412bb113a52354f73d9def4e5d540ba7fd (pinned; dated 2026-10-26)
#   Patch: patches/dave-pr-3288.patch (9-line, two-file delta; see patches/README.md)
# dev still declares VersionPrefix 3.20.2, so with IsTagBuild=true the components pack
# at exactly 3.20.2 and the app's unchanged
# `<PackageReference Include="Discord.Net" Version="3.20.2" />` resolves the patched
# Discord.Net.WebSocket / Discord.Net.Dave assemblies locally while the Discord.Net
# metapackage shell still comes from nuget.org.
# CLEANUP once upstream ships the fix: `grep DAVE-PR-3288` and delete these blocks,
# then delete patches/. Nothing else in this Dockerfile depends on this stage.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS discordnet
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /dnet
# Fetch ONLY the pinned dev commit, shallow (depth 1), rather than cloning dev's full
# history. A full clone pulls the entire Discord.Net git history (hundreds of MB) just to
# reach one commit, which is both slow and a disk-space risk on the build host. Fetching the
# exact SHA at depth 1 lands the same tree in a few MB. Pinning the SHA (not the moving dev
# HEAD) keeps the build reproducible.
RUN git init -q . \
    && git remote add origin https://github.com/discord-net/Discord.Net.git \
    && git fetch --depth 1 origin bf159d412bb113a52354f73d9def4e5d540ba7fd \
    && git checkout -q FETCH_HEAD \
    && test "$(git rev-parse HEAD)" = "bf159d412bb113a52354f73d9def4e5d540ba7fd"
COPY patches/dave-pr-3288.patch /tmp/dave-pr-3288.patch
RUN git apply --check /tmp/dave-pr-3288.patch && git apply /tmp/dave-pr-3288.patch
# Pack every Discord.Net component the app pulls in (directly or transitively via the
# Discord.Net metapackage) at version 3.20.2 into /packages. dev's VersionPrefix is
# still 3.20.2, and IsTagBuild=true strips the -dev VersionSuffix so the packages are
# exactly 3.20.2 (matching the app's unchanged PackageReference).
#
# Discord.Net's projects set GenerateDocumentationFile + TreatWarningsAsErrors, so a missing
# XML doc-comment (CS1591) is promoted to an error and fails the pack. The newer .NET 10 SDK
# also prunes some implicit framework packages, raising NU1510. Neither matters for a throwaway
# from-source build whose only purpose is to compile PR #3288's two-line DAVE fix: we are not
# shipping these as documented public packages. So we disable TreatWarningsAsErrors and silence
# the doc/restore warnings for the pack. This affects ONLY this temporary Discord.Net build;
# the app's own -warnaserror settings are untouched.
RUN set -eux \
    && for proj in \
         src/Discord.Net.Core/Discord.Net.Core.csproj \
         src/Discord.Net.Rest/Discord.Net.Rest.csproj \
         tools/Discord.Net.Dave/Discord.Net.Dave.csproj \
         src/Discord.Net.WebSocket/Discord.Net.WebSocket.csproj \
         src/Discord.Net.Commands/Discord.Net.Commands.csproj \
         src/Discord.Net.Webhook/Discord.Net.Webhook.csproj \
         src/Discord.Net.Interactions/Discord.Net.Interactions.csproj ; do \
       dotnet pack "$proj" -c Release \
         /p:IsTagBuild=true \
         /p:TreatWarningsAsErrors=false \
         /p:GenerateDocumentationFile=false \
         /p:NoWarn=CS1591%3BNU1510 \
         -o /packages ; \
     done
# ==== DAVE-PR-3288 END ======================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# DAVE-PR-3288: stage the patched local feed + NuGet.config BEFORE restore so the
# app's Discord.Net 3.20.2 reference resolves the patched assemblies from ./local-packages.
COPY --from=discordnet /packages/ ./local-packages/
COPY patches/NuGet.config ./NuGet.config
COPY ["JainaProudmoore.csproj", "./"]
RUN dotnet restore "JainaProudmoore.csproj"
COPY . .

FROM build AS publish
RUN dotnet publish "JainaProudmoore.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "JainaProudmoore.dll"]

