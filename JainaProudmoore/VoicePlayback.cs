using System.Diagnostics;
using Discord;
using Discord.Audio;

namespace JainaProudmoore;

/// <summary>
/// Joins a voice channel, plays a Gemini WAV through FFmpeg into Discord's PCM stream, then
/// leaves. Not unit tested (needs a real process, native audio libs, and a voice connection).
/// </summary>
public sealed class VoicePlayback(ILogger logger)
{
    private readonly ILogger _logger = logger;

    // Discord.Net's voice ConnectAsync returns the instant the AudioClient first raises "Connected"
    // (ConnectAudioAsync completes _audioConnectPromise from that event), but that connection can be
    // torn right back down a moment later: on the first voice connect of a session Discord commonly
    // emits a second VOICE_SERVER_UPDATE (region/server handoff), and Discord.Net responds by
    // restarting the AudioClient — ConnectionManager.StartAsync re-acquires its lock via StopAsync(),
    // which calls Cancel() and TrySetCanceled() on the in-flight connection promise. That surfaces as
    // the observed "Connected" -> TaskCanceledException (at ConnectionManager.StartAsync) -> teardown.
    // Crucially it fires on the ConnectionManager's internal background task AFTER the awaited
    // ConnectAsync has already returned, so it never throws out of ConnectAsync and a try/catch retry
    // around ConnectAsync can never see it. The only client-visible signal is the IAudioClient's
    // Disconnected event / ConnectionState leaving Connected. So we connect, then watch the connection
    // for a short settle window and only proceed once it has PROVEN stable; a drop inside the window
    // is a failed attempt that we tear down and reconnect.
    private const int MaxConnectAttempts = 4;

    // How long a freshly-returned connection must stay Connected before we trust it enough to create
    // the PCM stream. Long enough to cover the second VOICE_SERVER_UPDATE handoff that tears the first
    // cold connect down right after the DAVE handshake; short enough not to stall a healthy join.
    private static readonly TimeSpan ConnectSettleWindow = TimeSpan.FromMilliseconds(1500);

    // Pause between a failed connect attempt and the next, so the voice server handoff settles before
    // we try again instead of racing the same teardown.
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(500);

    // Discord.Net's BufferedWriteStream only starts transmitting once its queue preloads to
    // (bufferMillis / 20ms) frames; with the 1000ms default a clip under ~1s of audio never
    // preloads, so nothing is sent AND FlushAsync spins forever on the stuck queue (the "silent +
    // hung on Disconnecting" symptom). A small buffer makes the preload threshold ~1 frame, so any
    // clip transmits and the queue drains. 40ms = 2 frames: enough to smooth jitter, short enough
    // that even a one-word answer plays.
    private const int BufferMillis = 40;

    // Upper bound on a single playback so a wedged pipe can never hang the voice connection
    // indefinitely. Generously larger than any capped Gemini TTS answer.
    private static readonly TimeSpan PlaybackTimeout = TimeSpan.FromMinutes(2);

    // Bounded wait for the buffered audio to drain before we stop the connection, so teardown
    // cannot hang on "Disconnecting" even if the encoder/encrypt pipeline stalls.
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Plays <paramref name="wavBytes"/> (Gemini's 24 kHz mono WAV) into <paramref name="channel"/>,
    /// transcoding to 48 kHz stereo s16le via FFmpeg, then disconnects.
    /// </summary>
    public async Task PlayWavAsync(IVoiceChannel channel, byte[] wavBytes, CancellationToken ct)
    {
        // Playback runs on its own timeout-bounded token rather than the worker lifetime token.
        // Threading the long-lived worker token straight through ConnectAsync + the whole stream
        // chain means any blip that cancels it kills audio mid-flight; this local token still
        // honours shutdown (it is linked to ct) but also guarantees a bounded playback.
        using var playbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        playbackCts.CancelAfter(PlaybackTimeout);
        var playbackToken = playbackCts.Token;

        // Start FFmpeg FIRST as a non-null local so a failed start cannot leak a voice
        // connection (the connect happens only after this line succeeds). The non-null local
        // is also what the writer closure captures, so there is no CS8602 under -warnaserror.
        var ffmpeg =
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments =
                        "-hide_banner -loglevel panic -i pipe:0 -ac 2 -f s16le -ar 48000 pipe:1",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                }
            ) ?? throw new InvalidOperationException("ffmpeg failed to start");

        IAudioClient? audioClient = null;
        Action? releaseConnection = null;
        try
        {
            // Connect only after FFmpeg started successfully. ConnectStableAsync returns a connection
            // that has already proven stable through the post-handshake settle window, so the first
            // cold connect's self-inflicted teardown cannot silently kill playback. The returned token
            // fires if the connection drops later, so a mid-stream disconnect surfaces instead of
            // writing into a dead transport.
            var connection = await ConnectStableAsync(channel, playbackToken);
            audioClient = connection.Client;
            releaseConnection = connection.Release;

            // Cancel playback if the (now-stable) connection drops mid-stream; still honours the
            // playback timeout and shutdown via the linked playback token.
            using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(
                playbackToken,
                connection.DisconnectedToken
            );
            var streamToken = streamCts.Token;

            // bufferMillis kept small so short clips preload and transmit (see BufferMillis).
            var pcm = audioClient.CreatePCMStream(AudioApplication.Mixed, bufferMillis: BufferMillis);

            try
            {
                // One writer task: push WAV into FFmpeg stdin, flush, then close stdin so FFmpeg
                // reaches EOF and closes stdout. Captures the non-null local for the nullable check.
                var proc = ffmpeg;
                var writer = Task.Run(
                    async () =>
                    {
                        var stdin = proc.StandardInput.BaseStream;
                        await stdin.WriteAsync(wavBytes, streamToken);
                        await stdin.FlushAsync(streamToken);
                        proc.StandardInput.Close();
                    },
                    streamToken
                );

                // Exactly one reader: FFmpeg stdout -> Discord PCM (encode -> DAVE encrypt -> UDP).
                await ffmpeg.StandardOutput.BaseStream.CopyToAsync(pcm, streamToken);
                await writer;

                // Wait for the buffered/encrypted frames to actually drain, but bound it so a
                // stalled pipeline cannot hang teardown. FlushAsync spins on the queue, so a timed
                // token is the only safe way to leave it.
                using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(streamToken);
                flushCts.CancelAfter(FlushTimeout);
                try
                {
                    await pcm.FlushAsync(flushCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Audio flush timed out; tearing the connection down anyway");
                }
            }
            finally
            {
                // Dispose the PCM stream BEFORE StopAsync: disposal cancels the buffered writer
                // thread and releases the encode/encrypt chain, so the connection is idle when we
                // leave. Doing this first is what keeps teardown from wedging on "Disconnecting".
                await pcm.DisposeAsync();
            }
        }
        finally
        {
            // StopAsync() is the authoritative leave (disposal of the client does NOT disconnect).
            if (audioClient is not null)
            {
                try
                {
                    await audioClient.StopAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to stop the voice connection cleanly");
                }
            }

            // Detach the stable-connection disconnect handler and dispose its token source. Done
            // after StopAsync so that leave's Disconnected event still finds a live CTS; Release is
            // idempotent and no-ops if we never reached a stable connection.
            releaseConnection?.Invoke();

            // Ensure FFmpeg exits: bounded wait then kill the process tree. Uses ct (not the
            // playback token) so teardown still runs even once playback has been cancelled.
            try
            {
                using var killCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                killCts.CancelAfter(TimeSpan.FromSeconds(5));
                await ffmpeg.WaitForExitAsync(killCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ffmpeg did not exit cleanly; killing the process tree");
                try
                {
                    if (!ffmpeg.HasExited)
                    {
                        ffmpeg.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception killEx)
                {
                    _logger.LogWarning(killEx, "Failed to kill the ffmpeg process");
                }
            }

            ffmpeg.Dispose();

            // Dispose the audio client LAST, after StopAsync has performed the leave.
            audioClient?.Dispose();
        }
    }

    /// <summary>
    /// A voice connection that has proven stable through the post-connect settle window, paired with
    /// a token that fires if it later drops so playback can observe a mid-stream disconnect.
    /// <see cref="Release"/> detaches the disconnect handler and disposes the backing token source;
    /// the caller invokes it once playback teardown is complete.
    /// </summary>
    private sealed record StableConnection(
        IAudioClient Client,
        CancellationToken DisconnectedToken,
        Action Release
    );

    /// <summary>
    /// Connects to <paramref name="channel"/> and returns only once the connection has stayed
    /// <see cref="ConnectionState.Connected"/> through <see cref="ConnectSettleWindow"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ConnectAsync</c> completes as soon as the AudioClient first raises "Connected", but on the
    /// first cold connect of a session Discord.Net frequently tears that connection straight back
    /// down (a second VOICE_SERVER_UPDATE makes it restart the client, cancelling the in-flight
    /// connection promise). That cancellation lands on the ConnectionManager's internal background
    /// task, never on the awaited <c>ConnectAsync</c>, so it cannot be caught around the connect
    /// call. We instead watch the client's <see cref="IAudioClient.Disconnected"/> event and
    /// <see cref="IAudioClient.ConnectionState"/>: if it drops inside the settle window we tear the
    /// doomed client down and reconnect; otherwise we hand back a connection known to be alive.
    /// </para>
    /// </remarks>
    private async Task<StableConnection> ConnectStableAsync(
        IVoiceChannel channel,
        CancellationToken ct
    )
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // disconnect:true is the verified default; passed explicitly for self-documentation.
            // ConnectAsync takes no CancellationToken, so it is not itself interruptible; the bounded
            // playback token (and thus ct) still gates the loop and the settle wait around it.
            //
            // selfDeaf:true because /askjaina is SEND-ONLY: Jaina transcodes a WAV and transmits it,
            // and never consumes incoming audio. Joining deafened tells Discord not to send other
            // members' voice packets, which stops Discord.Net from spinning up per-speaker DAVE
            // receive-decode streams that spam "Failed to decrypt audio packet / DecryptionFailure"
            // (the inbound E2EE decode path is unused here and flaky under DAVE). It has no effect on
            // our outbound PCM stream.
            var client = await channel.ConnectAsync(
                selfDeaf: true,
                selfMute: false,
                external: false,
                disconnect: true
            );

            // Signalled the moment this client reports a disconnect. Lives for the whole connection:
            // it is the same token handed back to playback so a later drop surfaces there too.
            var disconnectedCts = new CancellationTokenSource();

            Task OnDisconnected(Exception _)
            {
                disconnectedCts.Cancel();
                return Task.CompletedTask;
            }

            client.Disconnected += OnDisconnected;

            // Set once we have handed ownership of the client + token source to a returned
            // StableConnection, so the finally below leaves them alone (the caller releases them).
            var handedOff = false;
            try
            {
                // Settle window: wait for a disconnect, cancellation, or the window to elapse. We
                // link ct so shutdown/timeout still breaks the wait.
                using (
                    var settleCts = CancellationTokenSource.CreateLinkedTokenSource(
                        ct,
                        disconnectedCts.Token
                    )
                )
                {
                    try
                    {
                        await Task.Delay(ConnectSettleWindow, settleCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // disconnectedCts fired: the connection dropped inside the window. Fall
                        // through to the stability check, which sees it is no longer Connected.
                    }
                }

                // Shutdown or the bounded playback timeout: do not retry, let it propagate (the
                // finally tears the in-flight client down).
                ct.ThrowIfCancellationRequested();

                if (
                    client.ConnectionState == ConnectionState.Connected
                    && !disconnectedCts.IsCancellationRequested
                )
                {
                    // Stable: hand back the live client plus its disconnect token so playback can
                    // observe a later drop. The Disconnected handler stays subscribed for the life of
                    // the connection; Release (invoked by the caller after teardown) detaches it and
                    // disposes the token source. Detaching before disposing guarantees our own
                    // StopAsync-driven Disconnected cannot fire into the disposed CTS.
                    var released = false;
                    void Release()
                    {
                        if (released)
                        {
                            return;
                        }
                        released = true;
                        client.Disconnected -= OnDisconnected;
                        disconnectedCts.Dispose();
                    }

                    handedOff = true;
                    return new StableConnection(client, disconnectedCts.Token, Release);
                }

                // Unstable: this is the observed first-cold-connect teardown. The finally tears the
                // doomed client down; here we only decide whether to retry.
                if (attempt >= MaxConnectAttempts)
                {
                    throw new InvalidOperationException(
                        $"Voice connection to '{channel.Name}' dropped during the settle window on "
                            + $"all {MaxConnectAttempts} attempts (first-connect teardown did not clear)."
                    );
                }

                _logger.LogWarning(
                    "Voice connect attempt {Attempt} of {Max} dropped before settling; reconnecting",
                    attempt,
                    MaxConnectAttempts
                );
                await Task.Delay(ConnectRetryDelay, ct);
            }
            finally
            {
                // Tear down the in-flight client on every path except a successful hand-off: the
                // retry loop, the exhausted-attempts throw, and any cancellation all leave through
                // here exactly once, so the client is never leaked and never double-disposed.
                if (!handedOff)
                {
                    client.Disconnected -= OnDisconnected;
                    await SafeStopAsync(client);
                    disconnectedCts.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Stops <paramref name="client"/> (the authoritative voice leave) without letting a teardown
    /// failure mask the real connect outcome, then disposes it.
    /// </summary>
    private async Task SafeStopAsync(IAudioClient client)
    {
        try
        {
            await client.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop an unstable voice connection cleanly");
        }

        client.Dispose();
    }
}
