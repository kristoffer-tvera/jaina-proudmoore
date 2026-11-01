// Package main is the jaina-voice-sidecar entrypoint.
//
// The bare `import "C"` below is pulled in transitively by golibdave (CGO). It
// documents that this binary links libdave via cgo and must be built with
// CGO_ENABLED=1 against a present libdave.so (see the Dockerfile and README).
// NOTE: there must be no comment directly attached above `import "C"` — cgo would
// compile such a comment as the C preamble.
package main

import "C"

import (
	"context"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/disgoorg/godave/golibdave"

	"github.com/disgoorg/disgo"
	"github.com/disgoorg/disgo/bot"
	"github.com/disgoorg/disgo/gateway"
	"github.com/disgoorg/disgo/voice"
)

func main() {
	logger := slog.Default()

	cfg, err := loadConfig()
	if err != nil {
		logger.Error("invalid configuration", slog.Any("err", err))
		os.Exit(1)
	}

	logger.Info("starting jaina-voice-sidecar", slog.String("disgo_version", disgo.Version))

	// Open our OWN gateway connection with the bot token. This is a send-only
	// voice bot: we enable the GuildVoiceStates intent (needed to join voice) and
	// wire DAVE via golibdave, but never register an OpusFrameReceiver, so we never
	// consume inbound audio.
	client, err := disgo.New(cfg.Token,
		bot.WithGatewayConfigOpts(gateway.WithIntents(gateway.IntentGuildVoiceStates)),
		bot.WithVoiceManagerConfigOpts(
			voice.WithDaveSessionCreateFunc(golibdave.NewSession),
		),
	)
	if err != nil {
		logger.Error("failed to create discord client", slog.Any("err", err))
		os.Exit(1)
	}

	openCtx, openCancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer openCancel()
	if err = client.OpenGateway(openCtx); err != nil {
		logger.Error("failed to open gateway", slog.Any("err", err))
		os.Exit(1)
	}
	logger.Info("gateway connected")

	srv := newServer(cfg, client, logger)
	httpServer := &http.Server{
		Addr:    "0.0.0.0:" + cfg.Port,
		Handler: srv.routes(),
	}

	// Run the HTTP server in the background so we can block on OS signals and shut
	// everything down cleanly.
	serverErr := make(chan error, 1)
	go func() {
		logger.Info("http server listening", slog.String("addr", httpServer.Addr))
		if err := httpServer.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			serverErr <- err
		}
	}()

	stop := make(chan os.Signal, 1)
	signal.Notify(stop, syscall.SIGINT, syscall.SIGTERM, os.Interrupt)

	select {
	case err := <-serverErr:
		logger.Error("http server failed", slog.Any("err", err))
	case sig := <-stop:
		logger.Info("shutdown signal received", slog.String("signal", sig.String()))
	}

	// Graceful shutdown: stop accepting HTTP requests, then close the gateway.
	shutdownCtx, shutdownCancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer shutdownCancel()
	if err := httpServer.Shutdown(shutdownCtx); err != nil {
		logger.Warn("http server shutdown error", slog.Any("err", err))
	}

	closeCtx, closeCancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer closeCancel()
	client.Close(closeCtx)
	logger.Info("shutdown complete")
}
