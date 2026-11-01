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

    /// <summary>
    /// Plays <paramref name="wavBytes"/> (Gemini's 24 kHz mono WAV) into <paramref name="channel"/>,
    /// transcoding to 48 kHz stereo s16le via FFmpeg, then disconnects.
    /// </summary>
    public async Task PlayWavAsync(IVoiceChannel channel, byte[] wavBytes, CancellationToken ct)
    {
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
            // Connect only after FFmpeg started successfully. disconnect:true is the verified
            // default; passed explicitly for self-documentation.
            audioClient = await channel.ConnectAsync(
                selfDeaf: false,
                selfMute: false,
                external: false,
                disconnect: true
            );

            await using AudioOutStream pcm = audioClient.CreatePCMStream(AudioApplication.Mixed);

            // Capture the non-null local into exactly one writer task: push WAV into FFmpeg
            // stdin, flush, then close stdin so FFmpeg reaches EOF and closes stdout.
            var proc = ffmpeg;
            var writer = Task.Run(
                async () =>
                {
                    var stdin = proc.StandardInput.BaseStream;
                    await stdin.WriteAsync(wavBytes, ct);
                    await stdin.FlushAsync(ct);
                    proc.StandardInput.Close();
                },
                ct
            );

            // Exactly one reader: FFmpeg stdout -> Discord PCM stream.
            await ffmpeg.StandardOutput.BaseStream.CopyToAsync(pcm, ct);

            await pcm.FlushAsync(ct);
            await writer;
        }
        finally
        {
            // StopAsync() FIRST: it is the authoritative leave (disposal does NOT disconnect).
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

            // Ensure FFmpeg exits: bounded wait then kill the process tree.
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
}
