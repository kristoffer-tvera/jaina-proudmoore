package main

import (
	"fmt"
	"os"
)

// config holds the sidecar's runtime configuration, sourced entirely from the
// environment. Secrets are never read from a file or baked into the image.
type config struct {
	// Token is the Discord bot token the sidecar uses to open its own gateway
	// connection. Required.
	Token string
	// Port is the TCP port the HTTP server binds on 0.0.0.0. Defaults to 8080.
	Port string
	// SidecarToken is an optional shared secret. When set, every /play request
	// must present it in the X-Sidecar-Token header.
	SidecarToken string
}

// loadConfig reads the configuration from the environment. It fails fast with a
// clear error when the required TOKEN is missing so a misconfigured container
// crashes immediately instead of silently running without a gateway.
func loadConfig() (config, error) {
	cfg := config{
		Token:        os.Getenv("TOKEN"),
		Port:         os.Getenv("PORT"),
		SidecarToken: os.Getenv("SIDECAR_TOKEN"),
	}

	if cfg.Token == "" {
		return config{}, fmt.Errorf("TOKEN environment variable is required but not set")
	}

	if cfg.Port == "" {
		cfg.Port = "8080"
	}

	return cfg, nil
}
