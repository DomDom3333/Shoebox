using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Shoebox.Web.Services;

/// <summary>
/// Video support is deliberately shallow: a clip is stored untouched for download and gets
/// one still frame so it has a tile in the grid. Nothing is transcoded and nothing is
/// streamed back for playback. The frame comes from ffmpeg when the host has it (the Docker
/// image ships it); without ffmpeg the upload still succeeds and the tile falls back to a
/// placeholder.
/// </summary>
public class VideoRenderer(IOptions<ShoeboxOptions> options, ImageRenderer images, ILogger<VideoRenderer> logger)
{
    /// <summary>Hard stop on a clip that ffmpeg can't make progress on, so an upload can't hang.</summary>
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Top-level atoms an ISO base media file (MP4 / M4V / MOV) can legitimately start with.
    /// </summary>
    private static readonly string[] IsoBoxTypes = ["ftyp", "moov", "mdat", "free", "skip", "wide", "pnot"];

    /// <summary>
    /// Grabs a poster frame and writes the same two WebP renditions a photo gets, so the grid
    /// and the lightbox need no video-specific handling. Returns null when no frame could be
    /// taken — the caller keeps the upload anyway.
    /// </summary>
    public async Task<ImageInfo?> RenderPosterAsync(string originalPath, string thumbPath, string displayPath,
        CancellationToken ct = default)
    {
        var framePath = Path.Combine(Path.GetTempPath(), $"shoebox_frame_{Guid.NewGuid():N}.png");
        try
        {
            // A clip shorter than the seek point yields no frame at all, so fall back to frame zero.
            var attempt = await ExtractFrameAsync(originalPath, framePath, options.Value.VideoPosterSeconds, ct);
            if (attempt is FrameResult.NoFrame)
            {
                attempt = await ExtractFrameAsync(originalPath, framePath, 0, ct);
            }

            return attempt is FrameResult.Extracted
                ? await images.ProcessAsync(framePath, thumbPath, displayPath, ct)
                : null;
        }
        finally
        {
            if (File.Exists(framePath))
            {
                File.Delete(framePath);
            }
        }
    }

    /// <summary>
    /// Cheap magic-byte check that the bytes really are one of the containers we accept, so a
    /// renamed file can't be stored and served back as a video. ffmpeg does the thorough
    /// validation when it's around; this holds the line when it isn't.
    /// </summary>
    public static bool LooksLikeVideo(string path)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = File.OpenRead(path);
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
        {
            return false;
        }

        // Matroska / WebM: an EBML header right at the start.
        if (header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3)
        {
            return true;
        }

        // MP4 / M4V / MOV: a 4-byte box length, then a 4-char box type.
        var boxType = System.Text.Encoding.ASCII.GetString(header[4..8]);
        return IsoBoxTypes.Contains(boxType, StringComparer.Ordinal);
    }

    /// <summary>
    /// Distinguishes "this clip gave us nothing at that point" (worth another try) from
    /// "there is no ffmpeg here" (retrying would only log the same warning twice).
    /// </summary>
    private enum FrameResult { Extracted, NoFrame, NoFfmpeg }

    private async Task<FrameResult> ExtractFrameAsync(string sourcePath, string framePath, double seconds,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(options.Value.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-nostdin", "-loglevel", "error", "-y",
                     "-ss", seconds.ToString(CultureInfo.InvariantCulture),
                     "-i", sourcePath,
                     "-frames:v", "1", "-an", "-f", "image2", framePath,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not run ffmpeg ({Path}); {Source} will have no poster frame",
                options.Value.FfmpegPath, sourcePath);
            return FrameResult.NoFfmpeg;
        }

        // Drain both pipes so ffmpeg can never block on a full buffer.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FrameTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            ct.ThrowIfCancellationRequested();
            logger.LogWarning("ffmpeg timed out taking a frame from {Source}", sourcePath);
            return FrameResult.NoFrame;
        }

        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
        {
            logger.LogWarning("ffmpeg exited {Code} taking a frame from {Source}: {Error}",
                process.ExitCode, sourcePath, stderr.Result.Trim());
            return FrameResult.NoFrame;
        }

        // ffmpeg can exit cleanly having written nothing (seeking past the end of a short clip).
        return File.Exists(framePath) && new FileInfo(framePath).Length > 0
            ? FrameResult.Extracted
            : FrameResult.NoFrame;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone, or we can't signal it; nothing useful left to do.
        }
    }
}
