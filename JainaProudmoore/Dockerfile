FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app
# Voice is handled by the separate jaina-voice-sidecar container; the bot no longer connects
# to voice itself, so it needs no audio codecs, ffmpeg, or libdave. Keep ca-certificates for
# TLS to Discord/Gemini at runtime.
RUN set -eux \
    && apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates \
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
