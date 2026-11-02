# syntax=docker/dockerfile:1

# ---- Build stage -----------------------------------------------------------
# golibdave links libdave over CGO, so the build needs a C toolchain and the
# libdave headers + shared object present at compile/link time.
# Debian trixie, NOT bookworm: the libdave v1.1.0 BoringSSL .so is built against
# glibc >= 2.38 (__isoc23_strto* symbols) and libstdc++ GLIBCXX_3.4.32
# (std::ios_base_library_init). Bookworm (glibc 2.36 / GCC 12) cannot link or load
# it; trixie (glibc 2.41 / GCC 14) satisfies both.
FROM golang:1.26-trixie AS build
WORKDIR /src

# Native build deps: a C toolchain for CGO, pkg-config (godave's lib.go resolves
# libdave via `#cgo pkg-config: dave`), plus curl/unzip to fetch libdave.
RUN set -eux \
    && apt-get update \
    && apt-get install -y --no-install-recommends build-essential pkg-config curl unzip ca-certificates \
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
    # godave's lib.go does `#cgo pkg-config: dave` and `#include "dave.h"`, so the
    # header must sit directly on the include path (flattened, no dave/ prefix) and
    # a dave.pc must exist for pkg-config. We mirror godave's scripts/libdave_install.sh
    # exactly (it generates the same dave.pc) instead of invoking it, so the build
    # needs no interactive shell-profile step.
    && unzip -j -o /tmp/libdave.zip 'include/dave/dave.h' -d /usr/local/include \
    && unzip -j -o /tmp/libdave.zip 'lib/libdave.so' -d /usr/local/lib \
    && unzip -o /tmp/libdave.zip 'licenses/*' -d /usr/local/share/libdave-licenses \
    && rm -f /tmp/libdave.zip \
    && test -f /usr/local/lib/libdave.so \
    && test -f /usr/local/include/dave.h \
    && mkdir -p /usr/local/lib/pkgconfig \
    && printf '%s\n' \
         'prefix=/usr/local' \
         'exec_prefix=${prefix}' \
         'libdir=${exec_prefix}/lib' \
         'includedir=${prefix}/include' \
         '' \
         'Name: dave' \
         'Description: Discord Audio & Video End-to-End Encryption (DAVE) Protocol' \
         "Version: ${LIBDAVE_VERSION}" \
         'URL: https://github.com/discord/libdave' \
         'Libs: -L${libdir} -ldave -Wl,-rpath,${libdir}' \
         'Cflags: -I${includedir}' \
         > /usr/local/lib/pkgconfig/dave.pc \
    && ldconfig

# Copy the module files first for better layer caching. go.sum may be absent
# (the local build environment has no Go toolchain to run `go mod tidy`), so we
# generate/complete it in the build with `go mod tidy` below.
COPY go.mod ./
COPY go.sum* ./

COPY . .

# CGO on; pkg-config finds dave via the generated dave.pc in /usr/local/lib/pkgconfig.
# `go mod tidy` resolves and writes go.sum (pins are already fixed in go.mod, so
# nothing floats), then build.
ENV CGO_ENABLED=1
ENV PKG_CONFIG_PATH=/usr/local/lib/pkgconfig
RUN go mod tidy \
    && go build -o /out/jaina-voice-sidecar .

# ---- Runtime stage ---------------------------------------------------------
# glibc base (Debian slim). MUST match the build stage's trixie: libdave.so needs
# glibc >= 2.38 and libstdc++ GLIBCXX_3.4.32 at load time, which bookworm lacks.
# ffmpeg does the WAV->PCM transcode; ca-certificates for TLS to Discord.
FROM debian:trixie-slim AS final
WORKDIR /app

# libstdc++6 is libdave's C++ runtime (boringssl is statically bundled, but the
# C++ standard library is not). ffmpeg for WAV->PCM; ca-certificates for TLS.
RUN set -eux \
    && apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg ca-certificates libstdc++6 \
    && rm -rf /var/lib/apt/lists/*

# libdave.so on the loader path + its license texts (we redistribute the binary).
COPY --from=build /usr/local/lib/libdave.so /usr/local/lib/libdave.so
COPY --from=build /usr/local/share/libdave-licenses /usr/local/share/libdave-licenses
RUN ldconfig

COPY --from=build /out/jaina-voice-sidecar /app/jaina-voice-sidecar

ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["/app/jaina-voice-sidecar"]
