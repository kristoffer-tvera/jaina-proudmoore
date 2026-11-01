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

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["JainaProudmoore.csproj", "./"]
RUN dotnet restore "JainaProudmoore.csproj"
COPY . .

FROM build AS publish
RUN dotnet publish "JainaProudmoore.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "JainaProudmoore.dll"]

