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
    // emits a second VOICE_SERVER_UPDATE (region/server handoff). Discord.Net's SocketGuild handles it
    // in FinishConnectAudio, which re-runs RepopulateAudioStreamsAsync and then restarts the SAME
    // AudioClient via AudioClient.StartAsync -> ConnectionManager.StartAsync -> AcquireConnectionLock
    // -> StopAsync() -> Cancel(), which TrySetCanceled()s the in-flight connection promise. That is the
    // observed "Connected" -> TaskCanceledException (at ConnectionManager.<StartAsync>b__0) ->
    // Disconnecting -> Disconnected. Crucially:
    //   * It fires on the ConnectionManager's internal background task AFTER the awaited ConnectAsync
    //     has returned, so a try/catch retry around ConnectAsync can never see it (confirmed in pass 2).
    //   * FinishConnectAudio gates the restart behind WaitForDisconnectAsync(5s), so on a healthy cold
    //     connect the restart — and therefore the teardown — can land up to ~5s AFTER the first
    //     "Connected". A fixed 1.5s settle window (pass 2) hands the client back before that teardown
    //     ever happens, so the drop then surfaces mid-stream and cuts playback. That is exactly the
    //     recurring field failure this pass targets.
    //   * The restart does NOT abandon the connection: ConnectionManager's reconnect loop re-establishes
    //     the SAME IAudioClient against the handed-off server, raising "Connected" again. The library
    //     recovers on its own — so tearing the client down (pass 2) or cancelling playback FIGHTS that
    //     recovery. The right move is to RIDE the restart: keep the same client, wait for it to come
    //     back Connected, and only trust it once it has held Connected past the restart gate.
    // So we connect, then require the connection to hold ConnectionState.Connected CONTINUOUSLY for a
    // quiet period; every Disconnected re-arms the timer and we wait (bounded) for the library's own
    // reconnect instead of tearing the client down. Only once it stays quiet do we create the stream.

    // The connection must hold Connected continuously for this long before we trust it. It has to clear
    // FinishConnectAudio's WaitForDisconnectAsync(5s) restart gate plus the ensuing reconnect, so a
    // handoff teardown that lands ~5s after the first Connected is observed and ridden out rather than
    // slipping past as "stable". 7s comfortably covers the 5s gate + the 5ms delay + reconnect time.
    private static readonly TimeSpan StableQuietPeriod = TimeSpan.FromSeconds(7);

    // Overall budget for reaching a durably-stable connection, across any number of library-driven
    // restarts. Generous enough for one full teardown + reconnect cycle on a slow cold start, bounded
    // so a genuinely dead connection cannot hang playback forever.
    private static readonly TimeSpan StableConnectDeadline = TimeSpan.FromSeconds(30);

    // After a drop, how long to wait for the library's ConnectionManager reconnect loop to bring the
    // SAME IAudioClient back to Connected before giving up this attempt. Covers the reconnect backoff
    // (ConnectionManager starts at a 1s delay) plus the voice handshake.
    private static readonly TimeSpan ReconnectWait = TimeSpan.FromSeconds(10);

    // Cadence for polling ConnectionState while waiting for a reconnect. IAudioClient exposes no
    // "Connected" event we can await directly, so we poll; 100ms is responsive without busy-waiting.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

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
    /// Connects to <paramref name="channel"/> and returns only once the connection has held
    /// <see cref="ConnectionState.Connected"/> continuously for <see cref="StableQuietPeriod"/>,
    /// riding out any library-driven restart (the second VOICE_SERVER_UPDATE handoff) in between.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ConnectAsync</c> completes as soon as the AudioClient first raises "Connected", but on the
    /// first cold connect of a session Discord.Net's <c>SocketGuild.FinishConnectAudio</c> handles a
    /// second VOICE_SERVER_UPDATE by restarting the SAME AudioClient, which cancels the in-flight
    /// connection promise and surfaces the observed
    /// <c>TaskCanceledException at ConnectionManager.&lt;StartAsync&gt;b__0</c> -> Disconnecting ->
    /// Disconnected. That cancellation lands on the ConnectionManager's internal background task,
    /// never on the awaited <c>ConnectAsync</c>, so it cannot be caught around the connect call.
    /// </para>
    /// <para>
    /// The restart is gated behind <c>WaitForDisconnectAsync(5s)</c> inside <c>FinishConnectAudio</c>,
    /// so the teardown can land up to ~5s after the first "Connected" — well outside a short fixed
    /// settle window. But the restart does not abandon the connection: the ConnectionManager reconnect
    /// loop re-establishes the SAME <see cref="IAudioClient"/> against the handed-off server and raises
    /// "Connected" again. So rather than tear the client down (which fights that recovery), we keep it,
    /// treat every <see cref="IAudioClient.Disconnected"/> as "the library is restarting us", wait
    /// (bounded) for it to come back <see cref="ConnectionState.Connected"/>, and require it to hold
    /// Connected continuously for <see cref="StableQuietPeriod"/> — long enough to clear the 5s restart
    /// gate — before we trust it and create the PCM stream.
    /// </para>
    /// </remarks>
    private async Task<StableConnection> ConnectStableAsync(
        IVoiceChannel channel,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();

        // disconnect:true is the verified default; passed explicitly for self-documentation.
        // ConnectAsync takes no CancellationToken, so it is not itself interruptible; the bounded
        // playback token (and thus ct) still gates every wait below.
        //
        // selfDeaf:true because /askjaina is SEND-ONLY: Jaina transcodes a WAV and transmits it, and
        // never consumes incoming audio. selfDeaf is the correct voice-state for a send-only bot, but
        // note it does NOT suppress the inbound DAVE receive path in this build: Discord.Net's
        // SocketGuild.RepopulateAudioStreamsAsync / AddOrUpdateVoiceStateAsync create a per-speaker
        // input (decrypt) stream for every other member in the channel unconditionally, so the
        // "Failed to decrypt audio packet / DecryptionFailure" warnings are library-level noise we
        // cannot gate from here (see diagnosis5.md). They are harmless: DaveDecryptStream.WriteAsync
        // logs and drops the packet; it never throws or tears the connection down.
        var client = await channel.ConnectAsync(
            selfDeaf: true,
            selfMute: false,
            external: false,
            disconnect: true
        );

        // The SAME client is kept across every library-driven restart. This token lives for the whole
        // connection and is handed to playback so a genuine later drop surfaces there too; a drop while
        // we are still settling re-arms the stability wait instead of being treated as failure. The
        // handler captures the variable (not its value), so after a RearmDisconnect swap the next
        // Disconnected cancels the fresh source.
        var disconnectedCts = new CancellationTokenSource();

        Task OnDisconnected(Exception _)
        {
            disconnectedCts.Cancel();
            return Task.CompletedTask;
        }

        client.Disconnected += OnDisconnected;

        var handedOff = false;
        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(StableConnectDeadline);
            var deadlineToken = deadlineCts.Token;

            while (true)
            {
                // Honour shutdown / playback timeout directly; map the overall stability deadline to a
                // clear failure rather than a bare OperationCanceledException.
                ct.ThrowIfCancellationRequested();
                if (deadlineToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException(
                        $"Voice connection to '{channel.Name}' never held stable for "
                            + $"{StableQuietPeriod.TotalSeconds:0}s within the "
                            + $"{StableConnectDeadline.TotalSeconds:0}s budget (restart teardown did "
                            + "not settle)."
                    );
                }

                // If we are not currently Connected (e.g. mid-restart), wait for the library's reconnect
                // loop to bring the same client back up before timing the quiet period.
                if (client.ConnectionState != ConnectionState.Connected)
                {
                    await WaitForConnectedAsync(client, deadlineToken);
                    // Re-arm the disconnect source for the freshly-reconnected session.
                    disconnectedCts = RearmDisconnect(disconnectedCts);
                    continue;
                }

                // Connected: require it to stay Connected for the full quiet period. A Disconnected
                // inside the period cancels this wait; we then loop, wait for reconnect, and start the
                // quiet period over. Only elapsing the whole period uncancelled proves stability.
                using (
                    var quietCts = CancellationTokenSource.CreateLinkedTokenSource(
                        deadlineToken,
                        disconnectedCts.Token
                    )
                )
                {
                    try
                    {
                        await Task.Delay(StableQuietPeriod, quietCts.Token);
                    }
                    catch (OperationCanceledException)
                        when (disconnectedCts.IsCancellationRequested
                            && !deadlineToken.IsCancellationRequested
                        )
                    {
                        // Dropped inside the quiet period: this is the restart. Re-arm and ride it out.
                        _logger.LogWarning(
                            "Voice connection dropped during settle (library restart); awaiting reconnect"
                        );
                        disconnectedCts = RearmDisconnect(disconnectedCts);
                        continue;
                    }
                }

                // Shutdown / timeout propagates out of the Task.Delay above unhandled; only the restart
                // drop is caught. Re-check the hard token explicitly for clarity.
                ct.ThrowIfCancellationRequested();

                // Held Connected for the whole quiet period and never dropped: trust it. Hand back the
                // live client plus its current disconnect token so playback observes a genuine later
                // drop. The handler stays subscribed for the life of the connection; Release (invoked
                // by the caller after teardown) detaches it and disposes the token source. Capture the
                // live token source in a local so Release disposes whichever one is current at hand-off.
                if (client.ConnectionState == ConnectionState.Connected)
                {
                    var liveCts = disconnectedCts;
                    var released = false;
                    void Release()
                    {
                        if (released)
                        {
                            return;
                        }
                        released = true;
                        client.Disconnected -= OnDisconnected;
                        liveCts.Dispose();
                    }

                    handedOff = true;
                    return new StableConnection(client, liveCts.Token, Release);
                }

                // Raced a drop right at the boundary: loop to wait for the reconnect.
            }
        }
        finally
        {
            // Tear the client down on every non-hand-off path (deadline exhausted, shutdown, timeout):
            // leave through here exactly once so the client is never leaked or double-disposed. On a
            // successful hand-off the caller owns teardown via Release + StopAsync.
            if (!handedOff)
            {
                client.Disconnected -= OnDisconnected;
                await SafeStopAsync(client);
                disconnectedCts.Dispose();
            }
        }
    }

    /// <summary>
    /// Waits for <paramref name="client"/> to reach <see cref="ConnectionState.Connected"/> again
    /// after a library-driven restart, bounded by <see cref="ReconnectWait"/> and
    /// <paramref name="deadlineToken"/>. Returns as soon as the client is Connected.
    /// </summary>
    private static async Task WaitForConnectedAsync(
        IAudioClient client,
        CancellationToken deadlineToken
    )
    {
        // The ConnectionManager reconnect loop brings the SAME client back to Connected; IAudioClient
        // exposes no "Connected" event we can await, so we poll ConnectionState on a short cadence.
        // Bounded by the per-reconnect cap and the overall stability deadline so a connection that
        // never comes back cannot hang here.
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(deadlineToken);
        waitCts.CancelAfter(ReconnectWait);
        var waitToken = waitCts.Token;

        while (client.ConnectionState != ConnectionState.Connected)
        {
            waitToken.ThrowIfCancellationRequested();
            await Task.Delay(PollInterval, waitToken);
        }
    }

    /// <summary>
    /// Disposes the spent disconnect token source and returns a fresh one so the settle loop can
    /// observe the next library-driven restart cleanly.
    /// </summary>
    private static CancellationTokenSource RearmDisconnect(CancellationTokenSource spent)
    {
        spent.Dispose();
        return new CancellationTokenSource();
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
