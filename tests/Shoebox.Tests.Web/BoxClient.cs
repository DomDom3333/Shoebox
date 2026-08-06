using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Shoebox.Tests.Web;

public sealed record UploadResponse(string FileName, string Status, Guid? MediaId, string? Reason);

/// <summary>Drives the app over HTTP the way a browser does, for the integration tests.</summary>
public static class BoxClient
{
    public static HttpClient Create(ShoeboxWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    public static async Task<string> CreateBoxAsync(HttpClient client, string? password = null)
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

    public static async Task<HttpResponseMessage> PostRazorFormAsync(
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

    public static async Task<UploadResponse> UploadAsync(
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

        var response = await client.PostAsync($"/api/p/{code}/media", form);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<UploadEnvelope>();
        return Assert.Single(Assert.IsType<UploadEnvelope>(body).Results);
    }

    private sealed record UploadEnvelope(UploadResponse[] Results);
}
