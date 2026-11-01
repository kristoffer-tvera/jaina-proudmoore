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

    // The first DAVE/MLS handshake of a session adds enough latency that Discord.Net's voice
    // ConnectionManager cancels the connect before it completes (observed: "Connected" is logged,
    // then a TaskCanceledException tears it right back down). A warm second attempt beats the
    // timeout, so we retry ConnectAsync a few times to let the first slow handshake land.
    private const int MaxConnectAttempts = 3;

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
        try
        {
            // Connect only after FFmpeg started successfully. Retry to survive the first slow DAVE
            // handshake: a cancelled/failed first connect is exactly the observed first-invocation
            // failure, and reconnecting lands warm.
            audioClient = await ConnectWithRetryAsync(channel, playbackToken);

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
                        await stdin.WriteAsync(wavBytes, playbackToken);
                        await stdin.FlushAsync(playbackToken);
                        proc.StandardInput.Close();
                    },
                    playbackToken
                );

                // Exactly one reader: FFmpeg stdout -> Discord PCM (encode -> DAVE encrypt -> UDP).
                await ffmpeg.StandardOutput.BaseStream.CopyToAsync(pcm, playbackToken);
                await writer;

                // Wait for the buffered/encrypted frames to actually drain, but bound it so a
                // stalled pipeline cannot hang teardown. FlushAsync spins on the queue, so a timed
                // token is the only safe way to leave it.
                using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(playbackToken);
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
    /// Connects to <paramref name="channel"/>, retrying so the first slow DAVE/MLS handshake does
    /// not permanently fail the connect. Each failed attempt is logged and retried until
    /// <see cref="MaxConnectAttempts"/> is reached, then the last exception is rethrown.
    /// </summary>
    private async Task<IAudioClient> ConnectWithRetryAsync(
        IVoiceChannel channel,
        CancellationToken ct
    )
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // disconnect:true is the verified default; passed explicitly for self-documentation.
                return await channel.ConnectAsync(
                    selfDeaf: false,
                    selfMute: false,
                    external: false,
                    disconnect: true
                );
            }
            catch (Exception ex) when (attempt < MaxConnectAttempts && !ct.IsCancellationRequested)
            {
                // The first connect commonly fails/cancels while the slow initial DAVE handshake is
                // still settling; a warm retry lands. Only rethrow once attempts are exhausted.
                _logger.LogWarning(
                    ex,
                    "Voice connect attempt {Attempt} of {Max} failed; retrying",
                    attempt,
                    MaxConnectAttempts
                );
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
    }
}
