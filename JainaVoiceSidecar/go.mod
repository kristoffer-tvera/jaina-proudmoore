module github.com/kristoffer-tvera/jaina-voice-sidecar

go 1.26.0

require (
	// disgo is pinned to a master pseudo-version rather than the v0.19.6 tag that the
	// disgo/_examples/voice/godave go.mod names. The godave example calls conn.DAVE().Ready()
	// (needed for the godave #11 stage/v0-passthrough guard), but DAVE() was added to the
	// voice.Conn interface AFTER v0.19.6 and only exists on master; it does not compile at
	// v0.19.6. This is the minimum published disgo that builds the godave example as written.
	// godave stays at the example's v0.3.0 pairing. See README "Version pairing".
	github.com/disgoorg/disgo v0.19.7-0.20261101233420-18ac6df9b116
	github.com/disgoorg/godave/golibdave v0.3.0
	github.com/disgoorg/snowflake/v2 v2.0.3
	github.com/kazzmir/opus-go v1.7.0
)

require (
	github.com/disgoorg/godave v0.3.0 // indirect
	github.com/disgoorg/godave/libdave v0.3.0 // indirect
	github.com/disgoorg/json/v2 v2.0.0 // indirect
	github.com/disgoorg/omit v1.0.0 // indirect
	github.com/gorilla/websocket v1.5.3 // indirect
	github.com/klauspost/compress v1.19.0 // indirect
	github.com/sasha-s/go-csync v0.0.0-20240107134140-fcbab37b09ad // indirect
	golang.org/x/crypto v0.54.0 // indirect
	golang.org/x/sys v0.47.0 // indirect
)
