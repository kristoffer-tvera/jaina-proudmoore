# syntax=docker/dockerfile:1

# ---- Build stage -----------------------------------------------------------
# golibdave links libdave over CGO, so the build needs a C toolchain and the
# libdave headers + shared object present at compile/link time.
FROM golang:1.26-bookworm AS build
WORKDIR /src

# Native build deps: a C toolchain for CGO, plus curl/unzip to fetch libdave.
RUN set -eux \
    && apt-get update \
    && apt-get install -y --no-install-recommends build-essential curl unzip ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# libdave version is dictated by the godave module, NOT the task's literal
# v1.2.2: godave v0.3.0 pins libdave v1.1.0 in libdave/release.txt, and a
# mismatched libdave would fail the CGO link/load. See README "Version pairing".
# NOTE the tag path contains "/cpp" (the libdave release tag is "v1.1.0/cpp").
ARG LIBDAVE_VERSION=1.1.0
ARG LIBDAVE_ASSET=libdave-Linux-X64-boringssl.zip
RUN set -eux \
    && curl -fsSL -o /tmp/libdave.zip \
         "https://github.com/discord/libdave/releases/download/v${LIBDAVE_VERSION}/cpp/${LIBDAVE_ASSET}" \
    # Headers for the CGO compile, the shared object for the link, licenses for
    # redistribution. boringssl is statically bundled, so no extra OpenSSL dep.
    && unzip -j -o /tmp/libdave.zip 'include/dave/dave.h' -d /usr/local/include/dave \
    && unzip -j -o /tmp/libdave.zip 'lib/libdave.so' -d /usr/local/lib \
    && unzip -o /tmp/libdave.zip 'licenses/*' -d /usr/local/share/libdave-licenses \
    && rm -f /tmp/libdave.zip \
    && test -f /usr/local/lib/libdave.so \
    && ldconfig

# Copy the module files first for better layer caching. go.sum may be absent
# (the local build environment has no Go toolchain to run `go mod tidy`), so we
# generate/complete it in the build with `go mod tidy` below.
COPY go.mod ./
COPY go.sum* ./

COPY . .

# CGO on; link against libdave in /usr/local/lib. `go mod tidy` resolves and
# writes go.sum (pins are already fixed in go.mod, so nothing floats), then build.
ENV CGO_ENABLED=1
RUN go mod tidy \
    && go build -o /out/jaina-voice-sidecar .

# ---- Runtime stage ---------------------------------------------------------
# glibc base (Debian slim), matching the glibc boringssl libdave build. ffmpeg
# does the WAV->PCM transcode; ca-certificates for TLS to Discord.
FROM debian:bookworm-slim AS final
WORKDIR /app

RUN set -eux \
    && apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# libdave.so on the loader path + its license texts (we redistribute the binary).
COPY --from=build /usr/local/lib/libdave.so /usr/local/lib/libdave.so
COPY --from=build /usr/local/share/libdave-licenses /usr/local/share/libdave-licenses
RUN ldconfig

COPY --from=build /out/jaina-voice-sidecar /app/jaina-voice-sidecar

ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["/app/jaina-voice-sidecar"]
