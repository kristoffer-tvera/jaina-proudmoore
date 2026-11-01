FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app
# Discord.Net voice needs native libopus + libsodium; playback needs ffmpeg on PATH.
# Installed as root before the USER switch. libsodium soname varies by Debian release, so
# the install is resilient across trixie/bookworm candidates.
#
# Debian ships only the versioned sonames (libopus.so.0, libsodium.so.NN). Discord.Net's
# P/Invoke asks the loader for the UNVERSIONED names (libopus.so, libsodium.so), which
# normally exist only in the -dev packages. Create those symlinks so the loader resolves
# them at runtime without pulling in dev headers.
RUN set -eux \
    && apt-get update \
    && apt-get install -y --no-install-recommends libopus0 ffmpeg \
    && ( apt-get install -y --no-install-recommends libsodium23 \
      || apt-get install -y --no-install-recommends libsodium26 \
      || apt-get install -y --no-install-recommends 'libsodium*' ) \
    && rm -rf /var/lib/apt/lists/* \
    && for name in libopus libsodium; do \
         lib="$(find / -name "$name.so.*" -type f 2>/dev/null | head -n1)"; \
         if [ -n "$lib" ]; then ln -sf "$lib" "$(dirname "$lib")/$name.so"; fi; \
       done \
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

