using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Xunit;
using static Shoebox.Tests.Web.BoxClient;

namespace Shoebox.Tests.Web;

/// <summary>
/// What storage encryption is supposed to buy, checked end to end: nothing on the data volume
/// is readable on its own, everything still serves correctly through the app, and an operator
/// who loses or changes the key is told so at startup instead of quietly serving broken boxes.
/// </summary>
public class StorageEncryptionTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAACgAAAAoCAYAAACM/rhtAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAABKSURBVFhH7c6hAcAgAMAwxmWcuNP20fDYGEQjq/qs9/vHxeYZbtOgalA1qBpUDaoGVYOqQdWgalA1qBpUDaoGVYOqQdWgalA1qDbzfwLv+8UzswAAAABJRU5ErkJggg==");

    /// <summary>
    /// A file big enough to span several 64 KiB chunks, so range reads have boundaries to get
    /// wrong. Starts with a real "ftyp" box so it clears the container check; ffmpeg finds no
    /// frame in it, which just means no poster.
    /// </summary>
    private static byte[] MultiChunkVideo(int size = 200_000)
    {
        var bytes = new byte[size];
        ReadOnlySpan<byte> header =
        [
            0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0x00, 0x00, 0x02, 0x00,
            (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'m', (byte)'p', (byte)'4', (byte)'2',
        ];
        header.CopyTo(bytes);
        for (var i = header.Length; i < size; i++)
        {
            bytes[i] = (byte)((i * 37) ^ (i >> 7));
        }

        return bytes;
    }

    private static string StoredFile(ShoeboxWebApplicationFactory factory, Guid mediaId, string folder) =>
        Assert.Single(
            Directory.EnumerateFiles(
                Path.Combine(factory.DataPath, "pools"),
                $"{mediaId:N}.*",
                SearchOption.AllDirectories),
            path => Path.GetFileName(Path.GetDirectoryName(path)) == folder);

    [Fact]
    public async Task Uploads_are_unreadable_on_disk_but_serve_intact()
    {
        using var factory = new ShoeboxWebApplicationFactory(ShoeboxWebApplicationFactory.NewKey());
        using var owner = Create(factory);
        var code = await CreateBoxAsync(owner);

        var added = await UploadAsync(owner, code, "Alice", "sample.png", Png);
        var mediaId = Assert.IsType<Guid>(added.MediaId);

        // On disk: our container, and no trace of the PNG that went in.
        var onDisk = await File.ReadAllBytesAsync(StoredFile(factory, mediaId, "orig"));
        Assert.Equal("SBXE"u8.ToArray(), onDisk[..4]);
        Assert.True(onDisk.AsSpan().IndexOf("PNG"u8) < 0, "the PNG signature is still readable on disk");
        Assert.True(onDisk.AsSpan().IndexOf(Png.AsSpan(0, 64)) < 0, "the original bytes are still on disk");

        // The thumbnail is a derived file and gets the same treatment.
        var thumbOnDisk = await File.ReadAllBytesAsync(StoredFile(factory, mediaId, "thumb"));
        Assert.Equal("SBXE"u8.ToArray(), thumbOnDisk[..4]);

        // Through the app, nothing has changed.
        var served = await owner.GetAsync($"/api/media/{mediaId}/original");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(Png, await served.Content.ReadAsByteArrayAsync());

        var thumb = await owner.GetAsync($"/api/media/{mediaId}/thumb");
        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        Assert.Equal("image/webp", thumb.Content.Headers.ContentType?.MediaType);

        // An upload passes through plaintext scratch on its way in; none of it may be left there.
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(factory.DataPath, "tmp")));
    }

    /// <summary>
    /// Re-rendering is the one operation that has to hand a stored original back to ImageMagick
    /// as a real file, so it decrypts to scratch and re-encrypts the results. Worth its own test
    /// because it is the only path that writes plaintext to the volume deliberately.
    /// </summary>
    [Fact]
    public async Task Reprocessing_re_encrypts_and_leaves_no_plaintext_behind()
    {
        using var factory = new ShoeboxWebApplicationFactory(ShoeboxWebApplicationFactory.NewKey());
        using var owner = Create(factory);
        var code = await CreateBoxAsync(owner);

        var mediaId = Assert.IsType<Guid>(
            (await UploadAsync(owner, code, "Alice", "sample.png", Png)).MediaId);

        var response = await owner.PostAsync($"/api/media/{mediaId}/reprocess", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var thumbOnDisk = await File.ReadAllBytesAsync(StoredFile(factory, mediaId, "thumb"));
        Assert.Equal("SBXE"u8.ToArray(), thumbOnDisk[..4]);

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(factory.DataPath, "tmp")));

        var thumb = await owner.GetAsync($"/api/media/{mediaId}/thumb");
        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
    }

    /// <summary>
    /// Range requests are the thing chunking exists for: video downloads and resumed transfers
    /// ask for slices, and the decrypting stream has to serve them without decrypting the file
    /// up to that point.
    /// </summary>
    [Theory]
    [InlineData(0, 99)]
    [InlineData(65_530, 65_545)]     // straddles the first chunk boundary
    [InlineData(150_000, 199_999)]   // the tail, several chunks in
    public async Task Range_requests_return_the_right_slice(int from, int to)
    {
        using var factory = new ShoeboxWebApplicationFactory(ShoeboxWebApplicationFactory.NewKey());
        using var owner = Create(factory);
        var code = await CreateBoxAsync(owner);

        var video = MultiChunkVideo();
        var added = await UploadAsync(owner, code, "Alice", "clip.mp4", video);
        var mediaId = Assert.IsType<Guid>(added.MediaId);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/media/{mediaId}/original");
        request.Headers.Range = new RangeHeaderValue(from, to);
        var response = await owner.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(video.AsSpan(from, to - from + 1).ToArray(), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(video.Length, response.Content.Headers.ContentRange?.Length);
    }

    [Fact]
    public async Task Whole_box_zip_holds_the_original_bytes()
    {
        using var factory = new ShoeboxWebApplicationFactory(ShoeboxWebApplicationFactory.NewKey());
        using var owner = Create(factory);
        var code = await CreateBoxAsync(owner);

        var video = MultiChunkVideo(120_000);
        await UploadAsync(owner, code, "Alice", "sample.png", Png);
        await UploadAsync(owner, code, "Bob", "clip.mp4", video);

        var response = await owner.GetAsync($"/api/p/{code}/zip");
        response.EnsureSuccessStatusCode();

        using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        Assert.Equal(2, archive.Entries.Count);
        Assert.Equal(Png, ReadEntry(archive, "Alice_sample.png"));
        Assert.Equal(video, ReadEntry(archive, "Bob_clip.mp4"));
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task Database_and_key_ring_are_encrypted_on_disk()
    {
        using var factory = new ShoeboxWebApplicationFactory(ShoeboxWebApplicationFactory.NewKey());
        using var owner = Create(factory);
        var code = await CreateBoxAsync(owner);
        await UploadAsync(owner, code, "Grandma Ruth", "sample.png", Png);

        var database = await File.ReadAllBytesAsync(Path.Combine(factory.DataPath, "shoebox.db"));

        Assert.True(
            database.AsSpan().IndexOf("SQLite format 3"u8) < 0,
            "the database still has a plaintext SQLite header");

        // The two things reading the database file would otherwise hand over: the share code,
        // which is a bearer credential for the box, and the names of everyone who uploaded.
        Assert.True(database.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(code)) < 0,
            "the box's share code is readable in the database file");
        Assert.True(database.AsSpan().IndexOf("Grandma Ruth"u8) < 0,
            "an uploader's name is readable in the database file");

        // The cookie-signing keys sign unlock and admin cookies; in the clear they would let
        // anyone with the volume mint a valid unlock cookie for any box. Unencrypted, the key
        // ring carries a <masterKey> element with the secret in it; ours is sealed instead.
        var keyRing = Directory.EnumerateFiles(Path.Combine(factory.DataPath, "keys"), "key-*.xml").ToList();
        var keyXml = await File.ReadAllTextAsync(Assert.Single(keyRing));
        Assert.Contains("encryptedKey", keyXml);
        Assert.DoesNotContain("<masterKey", keyXml);
        Assert.DoesNotContain("unencrypted form", keyXml);
    }

    /// <summary>
    /// The key ring has to be readable again after a restart, or every guest gets logged out of
    /// every password-protected box on each redeploy. An unlock cookie issued before the restart
    /// is the sharpest way to check the encrypted ring really round-trips.
    /// </summary>
    [Fact]
    public async Task Unlock_cookies_still_work_after_a_restart()
    {
        var key = ShoeboxWebApplicationFactory.NewKey();
        var dataPath = Path.Combine(Path.GetTempPath(), $"shoebox-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            string code;
            Guid mediaId;
            string unlockCookie;

            using (var before = new ShoeboxWebApplicationFactory(key, dataPath))
            {
                using var client = Create(before);
                code = await CreateBoxAsync(client, password: "festival-secret");
                mediaId = Assert.IsType<Guid>(
                    (await UploadAsync(client, code, "Alice", "sample.png", Png)).MediaId);

                // A guest, not the creator: the creator already holds an admin cookie, and it is
                // the guest's unlock cookie that has to survive the restart.
                using var guest = Create(before);
                var unlock = await PostRazorFormAsync(
                    guest, $"/p/{code}/unlock",
                    new Dictionary<string, string> { ["password"] = "festival-secret" });
                Assert.Equal(HttpStatusCode.Redirect, unlock.StatusCode);
                unlockCookie = string.Join("; ", unlock.Headers.GetValues("Set-Cookie")
                    .Select(value => value.Split(';')[0]));
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            using var after = new ShoeboxWebApplicationFactory(key, dataPath);
            using var returning = Create(after);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/media/{mediaId}/original");
            request.Headers.Add("Cookie", unlockCookie);
            var response = await returning.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dataPath, recursive: true);
        }
    }

    /// <summary>
    /// The upgrade path for an existing install: turning encryption on must not strand the data
    /// that is already there. Old files keep serving, the database is encrypted in place, and
    /// new uploads to the same box are encrypted.
    /// </summary>
    [Fact]
    public async Task Switching_encryption_on_keeps_existing_data_working()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"shoebox-upgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            string code;
            Guid existingId;

            using (var plain = new ShoeboxWebApplicationFactory(dataPath: dataPath))
            {
                using var client = Create(plain);
                code = await CreateBoxAsync(client);
                existingId = Assert.IsType<Guid>(
                    (await UploadAsync(client, code, "Alice", "before.png", Png)).MediaId);

                // Stored in the clear, as it was before any of this.
                var before = await File.ReadAllBytesAsync(StoredFile(plain, existingId, "orig"));
                Assert.Equal(Png, before);
            }

            using var encrypted = new ShoeboxWebApplicationFactory(
                ShoeboxWebApplicationFactory.NewKey(), dataPath);
            using var owner = Create(encrypted);

            // The database was migrated in place at startup, and nothing was left behind.
            var database = await File.ReadAllBytesAsync(Path.Combine(dataPath, "shoebox.db"));
            Assert.True(database.AsSpan().IndexOf("SQLite format 3"u8) < 0, "the database was not encrypted");
            Assert.False(File.Exists(Path.Combine(dataPath, "shoebox.db.plaintext-backup")),
                "a plaintext copy of the database was left on the volume");

            // The file uploaded before the switch is still plaintext on disk, and still serves.
            var served = await owner.GetAsync($"/api/media/{existingId}/original");
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.Equal(Png, await served.Content.ReadAsByteArrayAsync());

            // A new upload to that same box is encrypted.
            var afterId = Assert.IsType<Guid>(
                (await UploadAsync(owner, code, "Bob", "after.mp4", MultiChunkVideo(80_000))).MediaId);
            var after = await File.ReadAllBytesAsync(StoredFile(encrypted, afterId, "orig"));
            Assert.Equal("SBXE"u8.ToArray(), after[..4]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public async Task Refuses_to_start_with_the_wrong_key()
    {
        await AssertStartupFails(
            withKey: ShoeboxWebApplicationFactory.NewKey(),
            expectedInMessage: "wrong key");
    }

    [Fact]
    public async Task Refuses_to_start_when_the_key_is_taken_away()
    {
        await AssertStartupFails(
            withKey: null,
            expectedInMessage: "no encryption key is configured");
    }

    /// <summary>
    /// Writes a box under one key, then restarts over the same volume with
    /// <paramref name="withKey"/> and expects a refusal rather than a running app.
    /// Failing loudly here is the whole point: starting anyway would mean serving a broken
    /// gallery, or worse, writing new data under a second key.
    /// </summary>
    private static async Task AssertStartupFails(string? withKey, string expectedInMessage)
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"shoebox-keychange-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using (var original = new ShoeboxWebApplicationFactory(
                ShoeboxWebApplicationFactory.NewKey(), dataPath))
            {
                using var client = Create(original);
                await UploadAsync(client, await CreateBoxAsync(client), "Alice", "sample.png", Png);
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var failure = Record.Exception(() =>
            {
                using var restarted = new ShoeboxWebApplicationFactory(withKey, dataPath);
                using var client = Create(restarted);
            });

            Assert.NotNull(failure);
            Assert.Contains(expectedInMessage, failure.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
