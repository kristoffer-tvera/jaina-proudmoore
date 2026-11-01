package main

import (
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os/exec"
	"sync"

	"github.com/kazzmir/opus-go/opus"

	"github.com/disgoorg/disgo/voice"
)

const (
	sampleRate = 48000
	channels   = 2
)

// wavOpusProvider implements voice.OpusFrameProvider. It transcodes a WAV payload
// to 48kHz stereo s16le PCM via ffmpeg, then encodes each 20ms frame to Opus with
// the pure-Go kazzmir/opus-go encoder. The PCM->Opus mechanics mirror
// disgo/_examples/voice/mp3/main.go; the ffmpeg args mirror the (now deleted)
// .NET VoicePlayback transcode exactly.
type wavOpusProvider struct {
	logger  *slog.Logger
	cmd     *exec.Cmd
	stdout  io.ReadCloser
	encoder   *opus.Encoder
	eof       bool
	opusBuf   []byte
	closeOnce sync.Once
}

var _ voice.OpusFrameProvider = (*wavOpusProvider)(nil)

// newWavOpusProvider starts ffmpeg, feeds it the WAV bytes on stdin (closing stdin
// when done so ffmpeg reaches EOF), and returns a provider that reads decoded PCM
// from ffmpeg's stdout and re-encodes it to Opus.
func newWavOpusProvider(logger *slog.Logger, wav []byte) (*wavOpusProvider, error) {
	// Same transcode the deleted VoicePlayback.cs used: decode any container on
	// stdin, emit raw 48kHz stereo s16le on stdout.
	cmd := exec.Command("ffmpeg",
		"-hide_banner", "-loglevel", "panic",
		"-i", "pipe:0",
		"-ac", "2",
		"-f", "s16le",
		"-ar", "48000",
		"pipe:1",
	)

	stdin, err := cmd.StdinPipe()
	if err != nil {
		return nil, fmt.Errorf("ffmpeg stdin pipe: %w", err)
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return nil, fmt.Errorf("ffmpeg stdout pipe: %w", err)
	}

	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("start ffmpeg: %w", err)
	}

	// Feed the WAV to ffmpeg in a goroutine, then close stdin so ffmpeg sees EOF
	// and flushes/closes stdout. Errors here surface as a short/empty PCM stream,
	// which the reader turns into io.EOF.
	go func() {
		defer func() {
			if cerr := stdin.Close(); cerr != nil {
				logger.Debug("closing ffmpeg stdin", slog.Any("err", cerr))
			}
		}()
		if _, werr := stdin.Write(wav); werr != nil {
			logger.Warn("writing wav to ffmpeg", slog.Any("err", werr))
		}
	}()

	encoder, err := opus.NewEncoder(sampleRate, channels, opus.ApplicationVoIP)
	if err != nil {
		_ = cmd.Process.Kill()
		return nil, fmt.Errorf("create opus encoder: %w", err)
	}

	return &wavOpusProvider{
		logger:  logger,
		cmd:     cmd,
		stdout:  stdout,
		encoder: encoder,
		// 4000 bytes comfortably holds a single 20ms Opus packet.
		opusBuf: make([]byte, 4000),
	}, nil
}

// ProvideOpusFrame returns the next Opus frame, or io.EOF when the PCM stream is
// exhausted. A short final frame is zero-padded exactly like the disgo mp3 example.
func (p *wavOpusProvider) ProvideOpusFrame() ([]byte, error) {
	if p.eof {
		return nil, io.EOF
	}

	neededBytes := voice.OpusFrameSize * channels * 2
	pcmBytes := make([]byte, neededBytes)

	n, err := io.ReadFull(p.stdout, pcmBytes)
	if err == io.EOF || errors.Is(err, io.ErrUnexpectedEOF) {
		if n == 0 {
			p.eof = true
			return nil, io.EOF
		}
		// Zero-pad the trailing partial frame.
		for i := n; i < neededBytes; i++ {
			pcmBytes[i] = 0
		}
		p.eof = true
	} else if err != nil {
		return nil, fmt.Errorf("read pcm from ffmpeg: %w", err)
	}

	pcm := bytesToInt16LE(pcmBytes)

	nn, err := p.encoder.Encode(pcm, voice.OpusFrameSize, p.opusBuf)
	if err != nil {
		return nil, fmt.Errorf("encode opus: %w", err)
	}

	return p.opusBuf[:nn], nil
}

// Close kills ffmpeg (if still running) and closes the Opus encoder. It is safe to
// call more than once and from multiple goroutines (the AudioSender and the play
// lifecycle may both release it).
func (p *wavOpusProvider) Close() {
	p.closeOnce.Do(func() {
		if p.cmd != nil && p.cmd.Process != nil {
			_ = p.cmd.Process.Kill()
			_ = p.cmd.Wait()
		}
		if p.encoder != nil {
			p.encoder.Close()
		}
	})
}

// bytesToInt16LE reinterprets little-endian s16 PCM bytes as []int16.
func bytesToInt16LE(b []byte) []int16 {
	ints := make([]int16, len(b)/2)
	for i := range ints {
		ints[i] = int16(b[i*2]) | int16(b[i*2+1])<<8
	}
	return ints
}
