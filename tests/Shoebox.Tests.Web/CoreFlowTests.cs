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
