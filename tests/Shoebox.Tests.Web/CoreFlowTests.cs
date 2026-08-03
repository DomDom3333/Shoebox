using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Shoebox.Tests.Web;

public class CoreFlowTests
{
    private static readonly byte[] FortyPixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAACgAAAAoCAYAAACM/rhtAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAABKSURBVFhH7c6hAcAgAMAwxmWcuNP20fDYGEQjq/qs9/vHxeYZbtOgalA1qBpUDaoGVYOqQdWgalA1qBpUDaoGVYOqQdWgalA1qDbzfwLv+8UzswAAAABJRU5ErkJggg==");

    // Starts like a real MP4 (a 24-byte "ftyp" box) but carries no actual video, so it clears
    // the container check whether or not the host has ffmpeg to take a frame from it.
    private static readonly byte[] Mp4Header =
    [
        0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
        (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0x00, 0x00, 0x02, 0x00,
        (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'m', (byte)'p', (byte)'4', (byte)'2',
    ];

    [Fact]
    public async Task Video_upload_is_stored_downloadable_and_marked_in_the_gallery()
    {
        using var factory = new ShoeboxWebApplicationFactory();
        using var owner = CreateClient(factory);
        var code = await CreateBoxAsync(owner);

        var added = await UploadAsync(owner, code, "Alice", "clip.mp4", Mp4Header);
        Assert.Equal("added", added.Status);
        var photoId = Assert.IsType<Guid>(added.PhotoId);

        // No playback: the clip comes back as the stored original, byte for byte.
        var original = await owner.GetAsync($"/api/photos/{photoId}/original");
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.Equal("video/mp4", original.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Mp4Header, await original.Content.ReadAsByteArrayAsync());

        // Nothing to take a frame from, so the clip keeps its place in the box without a poster.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/photos/{photoId}/thumb")).StatusCode);

        var gallery = await owner.GetAsync($"/p/{code}");
        gallery.EnsureSuccessStatusCode();
        Assert.Contains("video-badge", await gallery.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/photos/{photoId}")).StatusCode);
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(factory.DataPath, "pools"),
            $"{photoId:N}.*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Real_clip_gets_a_poster_frame()
    {
        var clipPath = await MakeTestClipAsync();
        if (clipPath is null)
        {
            // No ffmpeg on this machine. The no-poster fallback is covered by the test above;
            // there is no way to synthesize a real clip to check the poster path here.
            return;
        }

        try
        {
            using var factory = new ShoeboxWebApplicationFactory();
            using var owner = CreateClient(factory);
            var code = await CreateBoxAsync(owner);

            var added = await UploadAsync(
                owner, code, "Alice", "clip.mp4", await File.ReadAllBytesAsync(clipPath));
            Assert.Equal("added", added.Status);
            var photoId = Assert.IsType<Guid>(added.PhotoId);

            var thumb = await owner.GetAsync($"/api/photos/{photoId}/thumb");
            Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
            Assert.Equal("image/webp", thumb.Content.Headers.ContentType?.MediaType);
            Assert.Equal(
                HttpStatusCode.OK,
                (await owner.GetAsync($"/api/photos/{photoId}/display")).StatusCode);
        }
        finally
        {
            File.Delete(clipPath);
        }
    }

    /// <summary>
    /// Synthesizes a two-second clip with ffmpeg, or returns null when the host has no ffmpeg.
    /// </summary>
    private static async Task<string?> MakeTestClipAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shoebox-test-{Guid.NewGuid():N}.mp4");
        var startInfo = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
                 {
                     "-nostdin", "-loglevel", "error", "-y",
                     "-f", "lavfi", "-i", "testsrc=duration=2:size=160x120:rate=10",
                     "-pix_fmt", "yuv420p", path,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo)!;
            await process.WaitForExitAsync();
            if (process.ExitCode == 0 && File.Exists(path))
            {
                return path;
            }
        }
        catch (Exception)
        {
            // ffmpeg isn't installed here.
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return null;
    }

    [Fact]
    public async Task File_that_is_not_really_a_video_is_rejected()
    {
        using var factory = new ShoeboxWebApplicationFactory();
        using var owner = CreateClient(factory);
        var code = await CreateBoxAsync(owner);

        var result = await UploadAsync(owner, code, "Alice", "not-really.mp4", FortyPixelPng);

        Assert.Equal("rejected", result.Status);
        Assert.Null(result.PhotoId);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(
                Path.Combine(factory.DataPath, "pools"),
                "*",
                SearchOption.AllDirectories),
            path => Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Protected_box_core_flow_enforces_access_and_cleans_up_deleted_photo()
    {
        using var factory = new ShoeboxWebApplicationFactory();
        using var owner = CreateClient(factory);
        var code = await CreateBoxAsync(owner, password: "festival-secret");

        var added = await UploadAsync(owner, code, "Alice", "sample.png", FortyPixelPng);
        Assert.Equal("added", added.Status);
        var photoId = Assert.IsType<Guid>(added.PhotoId);

        var thumb = await owner.GetAsync($"/api/photos/{photoId}/thumb");
        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        Assert.Equal("image/webp", thumb.Content.Headers.ContentType?.MediaType);

        using var guest = CreateClient(factory);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.GetAsync($"/api/photos/{photoId}/original")).StatusCode);

        var wrongUnlock = await PostRazorFormAsync(
            guest,
            $"/p/{code}/unlock",
            new Dictionary<string, string> { ["password"] = "wrong" });
        Assert.Equal(HttpStatusCode.OK, wrongUnlock.StatusCode);
        Assert.Contains("That password", await wrongUnlock.Content.ReadAsStringAsync());

        var correctUnlock = await PostRazorFormAsync(
            guest,
            $"/p/{code}/unlock",
            new Dictionary<string, string> { ["password"] = "festival-secret" });
        Assert.Equal(HttpStatusCode.Redirect, correctUnlock.StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await guest.GetAsync($"/api/photos/{photoId}/original")).StatusCode);

        var like = await guest.PostAsync($"/api/photos/{photoId}/like", content: null);
        Assert.Equal(HttpStatusCode.OK, like.StatusCode);
        var likeBody = await like.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(likeBody.GetProperty("liked").GetBoolean());
        Assert.Equal(1, likeBody.GetProperty("count").GetInt32());

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await guest.DeleteAsync($"/api/photos/{photoId}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await owner.DeleteAsync($"/api/photos/{photoId}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await owner.GetAsync($"/api/photos/{photoId}/original")).StatusCode);
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(factory.DataPath, "pools"),
            $"{photoId:N}.*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Corrupt_image_is_rejected_and_not_left_on_disk()
    {
        using var factory = new ShoeboxWebApplicationFactory();
        using var owner = CreateClient(factory);
        var code = await CreateBoxAsync(owner);

        var result = await UploadAsync(owner, code, "Alice", "broken.jpg", [1, 2, 3, 4]);

        Assert.Equal("rejected", result.Status);
        Assert.Null(result.PhotoId);
        Assert.Contains("read", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(
                Path.Combine(factory.DataPath, "pools"),
                "*",
                SearchOption.AllDirectories),
            path => Path.GetFileName(path).StartsWith("upload_", StringComparison.Ordinal));
    }

    private static HttpClient CreateClient(ShoeboxWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private static async Task<string> CreateBoxAsync(HttpClient client, string? password = null)
    {
        var response = await PostRazorFormAsync(
            client,
            "/Create",
            new Dictionary<string, string>
            {
                ["name"] = "Test box",
                ["description"] = "Integration test",
                ["password"] = password ?? "",
                ["expiryDays"] = "0"
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = Assert.IsType<Uri>(response.Headers.Location);
        var match = Regex.Match(location.OriginalString, @"/p/([^/]+)/admin");
        Assert.True(match.Success, $"Unexpected create redirect: {location}");
        return match.Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PostRazorFormAsync(
        HttpClient client,
        string path,
        Dictionary<string, string> fields)
    {
        var get = await client.GetAsync(path);
        get.EnsureSuccessStatusCode();
        var html = await get.Content.ReadAsStringAsync();
        var tokenMatch = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(tokenMatch.Success, $"No antiforgery token found at {path}.");
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(tokenMatch.Groups[1].Value);
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    private static async Task<UploadResponse> UploadAsync(
        HttpClient client,
        string code,
        string uploader,
        string fileName,
        byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(uploader), "uploaderName");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "files", fileName);

        var response = await client.PostAsync($"/api/p/{code}/photos", form);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<UploadEnvelope>();
        return Assert.Single(Assert.IsType<UploadEnvelope>(body).Results);
    }

    private sealed record UploadEnvelope(UploadResponse[] Results);
    private sealed record UploadResponse(
        string FileName,
        string Status,
        Guid? PhotoId,
        string? Reason);
}
