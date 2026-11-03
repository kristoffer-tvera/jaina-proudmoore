package main

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"sync"
	"time"

	"github.com/disgoorg/snowflake/v2"

	"github.com/disgoorg/disgo/bot"
	"github.com/disgoorg/disgo/voice"
)

// errDaveNotReady is returned when the DAVE session never reaches Ready() within
// the bounded wait. This is the godave #11 case: on stage channels (and any
// channel where the voice gateway acks dave_protocol_version 0 / passthrough)
// DAVE().Ready() stays false forever, and disgo's AudioSender refuses to send. We
// surface it as a distinct error so the HTTP layer can answer with a clear 4xx
// instead of hanging or reporting a generic failure.
var errDaveNotReady = errors.New("dave session not ready: stage channel or v0 passthrough; refusing to send")

const (
	// openTimeout bounds the voice-gateway join handshake.
	openTimeout = 20 * time.Second
	// daveReadyTimeout bounds how long we wait for DAVE().Ready() before giving up
	// (the godave #11 guard). Comfortably covers a healthy MLS handshake.
	daveReadyTimeout = 15 * time.Second
	// daveReadyPoll is the cadence for polling DAVE().Ready().
	daveReadyPoll = 100 * time.Millisecond
	// maxPlaybackTimeout caps a single playback so a wedged provider can never hold
	// the voice connection open indefinitely.
	maxPlaybackTimeout = 2 * time.Minute
	// drainDelay is how long we keep the connection open AFTER the provider reports
	// EOF, before leaving. The provider hitting io.EOF only means the last frame was
	// PULLED, not that it has been transmitted and played out: disgo's AudioSender
	// still needs to emit its 5 trailing silence frames (~100ms) and Discord's
	// receiver jitter buffer needs to play out the last real frames. Leaving
	// immediately on EOF cuts the audio off mid-sentence. This delay covers the
	// silence frames plus playout slack so the tail is actually heard.
	drainDelay = 1500 * time.Millisecond
	// leaveTimeout bounds the voice leave on teardown.
	leaveTimeout = 10 * time.Second
)

// playWav joins the given voice channel self-deafened (send-only), transcodes and
// transmits the WAV under DAVE, then always leaves. Every phase is bounded by its
// own timeout so a single stuck request cannot wedge the service. The channel is
// left via a deferred Close even on failure.
func playWav(ctx context.Context, client *bot.Client, logger *slog.Logger, guildID, channelID snowflake.ID, wav []byte) error {
	conn := client.VoiceManager.CreateConn(guildID)

	// Always leave the channel, even on error. Uses a fresh context so teardown
	// still runs when the request context is already done.
	defer func() {
		leaveCtx, cancel := context.WithTimeout(context.Background(), leaveTimeout)
		defer cancel()
		conn.Close(leaveCtx)
	}()

	// Join self-deafened and not self-muted: send-only bot. Open(ctx, channelID,
	// selfMute=false, selfDeaf=true).
	openCtx, openCancel := context.WithTimeout(ctx, openTimeout)
	defer openCancel()
	if err := conn.Open(openCtx, channelID, false, true); err != nil {
		return err
	}

	if err := conn.SetSpeaking(openCtx, voice.SpeakingFlagMicrophone); err != nil {
		return err
	}

	// Wait for DAVE to be Ready before sending. disgo's AudioSender only pulls
	// frames once DAVE().Ready() is true; on a stage / v0-passthrough channel it
	// never flips (godave #11), so bound the wait and return a clear error.
	if err := waitForDaveReady(ctx, conn, logger); err != nil {
		return err
	}

	// Build the WAV->Opus provider and wrap it so we can observe EOF: the
	// AudioSender started by SetOpusFrameProvider pulls frames asynchronously, so
	// we learn playback is done when the wrapper sees io.EOF.
	inner, err := newWavOpusProvider(logger, wav)
	if err != nil {
		return err
	}
	done := make(chan struct{})
	provider := &eofSignalingProvider{inner: inner, done: done}
	// Explicitly release the provider (kill ffmpeg, close the encoder) when we
	// return. conn.Close only tears down the AudioSender asynchronously via the
	// voice-state update, so closing here guarantees no leaked ffmpeg process.
	defer provider.Close()

	conn.SetOpusFrameProvider(provider)

	// Wait for the provider to reach EOF (all frames pulled) or the bounded
	// playback timeout / request cancellation.
	playCtx, playCancel := context.WithTimeout(ctx, maxPlaybackTimeout)
	defer playCancel()
	select {
	case <-done:
		// EOF means the last frame was PULLED, not played out. Hold the connection
		// open for drainDelay so the AudioSender's trailing silence frames and
		// Discord's jitter buffer can play out the tail before the deferred
		// conn.Close() leaves the channel. Still honour request cancellation.
		logger.Info("playback drained; holding briefly before leaving", slog.Duration("drain", drainDelay))
		select {
		case <-time.After(drainDelay):
		case <-ctx.Done():
		}
		logger.Info("playback complete")
		return nil
	case <-playCtx.Done():
		return playCtx.Err()
	}
}

// waitForDaveReady polls conn.DAVE().Ready() until it is true, the request is
// cancelled, or daveReadyTimeout elapses. A timeout maps to errDaveNotReady.
func waitForDaveReady(ctx context.Context, conn voice.Conn, logger *slog.Logger) error {
	readyCtx, cancel := context.WithTimeout(ctx, daveReadyTimeout)
	defer cancel()

	ticker := time.NewTicker(daveReadyPoll)
	defer ticker.Stop()

	for {
		if conn.DAVE().Ready() {
			return nil
		}
		select {
		case <-readyCtx.Done():
			if ctx.Err() != nil {
				// Caller cancelled / request timed out.
				return ctx.Err()
			}
			logger.Warn("dave session never became ready", slog.Duration("waited", daveReadyTimeout))
			return errDaveNotReady
		case <-ticker.C:
		}
	}
}

// eofSignalingProvider wraps a voice.OpusFrameProvider and closes done the first
// time the inner provider returns io.EOF, so the play loop can learn that the
// AudioSender has drained the stream.
type eofSignalingProvider struct {
	inner    voice.OpusFrameProvider
	done     chan struct{}
	doneOnce sync.Once
}

var _ voice.OpusFrameProvider = (*eofSignalingProvider)(nil)

func (p *eofSignalingProvider) ProvideOpusFrame() ([]byte, error) {
	frame, err := p.inner.ProvideOpusFrame()
	if errors.Is(err, io.EOF) {
		p.doneOnce.Do(func() { close(p.done) })
	}
	return frame, err
}

func (p *eofSignalingProvider) Close() {
	p.inner.Close()
}
