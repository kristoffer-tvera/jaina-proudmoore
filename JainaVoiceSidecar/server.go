package main

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"net/http"
	"time"

	"github.com/disgoorg/snowflake/v2"

	"github.com/disgoorg/disgo/bot"
)

// maxWavBytes caps the request body to guard against unbounded uploads. A capped
// TTS clip is far smaller than this.
const maxWavBytes = 25 << 20 // 25 MiB

// requestTimeout bounds a single /play request end to end (join + DAVE + playback
// + leave). Generous enough for the longest capped clip, bounded so a stuck
// request cannot pile up.
const requestTimeout = 3 * time.Minute

type server struct {
	cfg    config
	client *bot.Client
	logger *slog.Logger
}

func newServer(cfg config, client *bot.Client, logger *slog.Logger) *server {
	return &server{cfg: cfg, client: client, logger: logger}
}

func (s *server) routes() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", s.handleHealthz)
	mux.HandleFunc("POST /play", s.handlePlay)
	return mux
}

func (s *server) handleHealthz(w http.ResponseWriter, _ *http.Request) {
	w.WriteHeader(http.StatusOK)
	_, _ = io.WriteString(w, "ok")
}

// handlePlay accepts a raw WAV body and plays it into the requested voice channel.
//
//	POST /play?guildId=<snowflake>&channelId=<snowflake>
//	Body: raw WAV bytes (Content-Type audio/wav or application/octet-stream)
//	Header: X-Sidecar-Token (required only when SIDECAR_TOKEN is configured)
func (s *server) handlePlay(w http.ResponseWriter, r *http.Request) {
	// Optional shared-secret auth.
	if s.cfg.SidecarToken != "" {
		if r.Header.Get("X-Sidecar-Token") != s.cfg.SidecarToken {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}
	}

	guildID, err := snowflake.Parse(r.URL.Query().Get("guildId"))
	if err != nil {
		http.Error(w, "invalid or missing guildId", http.StatusBadRequest)
		return
	}
	channelID, err := snowflake.Parse(r.URL.Query().Get("channelId"))
	if err != nil {
		http.Error(w, "invalid or missing channelId", http.StatusBadRequest)
		return
	}

	wav, err := io.ReadAll(io.LimitReader(r.Body, maxWavBytes+1))
	if err != nil {
		http.Error(w, "failed to read request body", http.StatusBadRequest)
		return
	}
	if len(wav) == 0 {
		http.Error(w, "empty request body", http.StatusBadRequest)
		return
	}
	if len(wav) > maxWavBytes {
		http.Error(w, "request body too large", http.StatusRequestEntityTooLarge)
		return
	}

	ctx, cancel := context.WithTimeout(r.Context(), requestTimeout)
	defer cancel()

	if err := playWav(ctx, s.client, s.logger, guildID, channelID, wav); err != nil {
		s.writePlayError(w, guildID, channelID, err)
		return
	}

	w.WriteHeader(http.StatusOK)
	_, _ = io.WriteString(w, "ok")
}

// writePlayError maps a playWav failure to an appropriate status code.
func (s *server) writePlayError(w http.ResponseWriter, guildID, channelID snowflake.ID, err error) {
	s.logger.Error("play failed",
		slog.String("guild_id", guildID.String()),
		slog.String("channel_id", channelID.String()),
		slog.Any("err", err),
	)

	switch {
	case errors.Is(err, errDaveNotReady):
		// godave #11: cannot encrypt on this channel (stage / v0 passthrough).
		http.Error(w, err.Error(), http.StatusUnprocessableEntity)
	case errors.Is(err, context.DeadlineExceeded):
		http.Error(w, "playback timed out", http.StatusGatewayTimeout)
	default:
		http.Error(w, "failed to play audio", http.StatusBadGateway)
	}
}
