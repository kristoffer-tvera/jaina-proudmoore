FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app
# Discord.Net voice needs native libopus + libsodium; playback needs ffmpeg on PATH.
# Installed as root before the USER switch. libsodium soname varies by Debian release, so
# the install is resilient across trixie/bookworm candidates.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libopus0 ffmpeg \
    && ( apt-get install -y --no-install-recommends libsodium23 \
      || apt-get install -y --no-install-recommends libsodium26 \
      || apt-get install -y --no-install-recommends 'libsodium*' ) \
    && rm -rf /var/lib/apt/lists/*
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

